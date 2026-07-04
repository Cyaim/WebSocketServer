using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Hook the dynamically emitted "MaxMind.GeoIP2" assembly calls back into.
    /// Behaviour is keyed by database path so concurrently running tests that
    /// construct MaxMind providers with unknown paths keep getting a null reader
    /// (the emitted DatabaseReader constructor throws for unregistered paths).
    /// </summary>
    public static class MaxMindHook
    {
        public static readonly ConcurrentDictionary<string, Func<IPAddress, object>> CityHandlers =
            new ConcurrentDictionary<string, Func<IPAddress, object>>(StringComparer.OrdinalIgnoreCase);

        public static void OnCreate(string path)
        {
            if (path == null || !CityHandlers.ContainsKey(path))
            {
                throw new IOException("MaxMindHook: unknown database path " + path);
            }
        }

        public static object OnCity(string path, IPAddress ip)
        {
            return CityHandlers[path](ip);
        }
    }

    /// <summary>
    /// Line-coverage tests for the offline geo providers:
    /// MaxMindOfflineGeoLocationProvider (reflection-driven reader, fake assembly emitted at runtime),
    /// ChunZhenOfflineGeoLocationProvider (crafted qqwry.dat binaries),
    /// IpIpNetOfflineGeoLocationProvider (private parser helpers via reflection).
    /// </summary>
    [Collection("StaticState")]
    public class GeoOfflineCovTests : IDisposable
    {
        private readonly string _tempDir;

        public GeoOfflineCovTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "cyaim-geocov-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        private string WriteFile(string name, byte[] content)
        {
            var path = Path.Combine(_tempDir, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        private string NewDbPath(string ext)
        {
            var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ext);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            return path;
        }

        #region Shared helpers

        /// <summary>Logger whose Debug-level Log throws, to force the provider's outer catch.</summary>
        private sealed class ThrowOnDebugLogger<T> : ILogger<T>
        {
            public IDisposable BeginScope<TState>(TState state) => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (logLevel == LogLevel.Debug)
                {
                    throw new InvalidOperationException("debug-log-throws");
                }
            }
        }

        private static object GetField(object instance, string name)
        {
            var f = instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(f);
            return f.GetValue(instance);
        }

        private static void SetField(object instance, string name, object value)
        {
            var f = instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(f);
            f.SetValue(instance, value);
        }

        private static object InvokePrivate(object instance, string name, params object[] args)
        {
            var m = instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(m);
            try
            {
                return m.Invoke(instance, args);
            }
            catch (TargetInvocationException tie)
            {
                throw tie.InnerException ?? tie;
            }
        }

        /// <summary>
        /// Drives the "already loaded" re-check inside the lock of EnsureDatabaseLoadedAsync:
        /// hold the provider's private lock, start a background call that passes the first
        /// check and blocks on the lock, then flip _databaseLoaded before releasing.
        /// </summary>
        private static void CoverDoubleCheckedLoadBranch(object provider)
        {
            var lockObj = GetField(provider, "_lockObject");
            var loadedField = provider.GetType().GetField("_databaseLoaded", BindingFlags.NonPublic | BindingFlags.Instance);
            var method = provider.GetType().GetMethod("EnsureDatabaseLoadedAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(loadedField);
            Assert.NotNull(method);

            loadedField.SetValue(provider, false);
            Task inner;
            var started = new ManualResetEventSlim(false);
            Monitor.Enter(lockObj);
            try
            {
                inner = Task.Run(() =>
                {
                    started.Set();
                    ((Task)method.Invoke(provider, null)).GetAwaiter().GetResult();
                });
                Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
                // Give the background call time to pass the first check and block on the lock.
                Thread.Sleep(200);
                loadedField.SetValue(provider, true);
            }
            finally
            {
                Monitor.Exit(lockObj);
            }

            Assert.True(inner.Wait(TimeSpan.FromSeconds(10)));
            // A subsequent call takes the fast path (first check already true).
            ((Task)method.Invoke(provider, null)).GetAwaiter().GetResult();
        }

        #endregion

        #region MaxMind — fake response object graph

        public sealed class FakeCountry
        {
            public string Name { get; set; }
            public string IsoCode { get; set; }
        }

        public sealed class FakeSubdivision
        {
            public string Name { get; set; }
        }

        public sealed class FakeCity
        {
            public string Name { get; set; }
        }

        public sealed class FakeLocation
        {
            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
        }

        public sealed class FakeCityResponse
        {
            public FakeCountry Country { get; set; }
            public List<FakeSubdivision> Subdivisions { get; set; }
            public FakeCity City { get; set; }
            public FakeLocation Location { get; set; }
        }

        private static FakeCityResponse RichResponse() => new FakeCityResponse
        {
            Country = new FakeCountry { Name = "United States", IsoCode = "US" },
            Subdivisions = new List<FakeSubdivision> { new FakeSubdivision { Name = "California" } },
            City = new FakeCity { Name = "Mountain View" },
            Location = new FakeLocation { Latitude = 37.4, Longitude = -122.1 }
        };

        /// <summary>Reader with a City(IPAddress) method that delegates to a Func.</summary>
        public sealed class FakeReader
        {
            private readonly Func<IPAddress, object> _city;
            public FakeReader(Func<IPAddress, object> city) { _city = city; }
            public object City(IPAddress ip) => _city(ip);
        }

        private MaxMindOfflineGeoLocationProvider NewMaxMind(ILogger<MaxMindOfflineGeoLocationProvider> logger = null)
        {
            return new MaxMindOfflineGeoLocationProvider(
                logger ?? NullLogger<MaxMindOfflineGeoLocationProvider>.Instance,
                NewDbPath(".mmdb"));
        }

        private MaxMindOfflineGeoLocationProvider NewMaxMindWithReader(object reader)
        {
            var provider = NewMaxMind();
            SetField(provider, "_databaseReader", reader);
            SetField(provider, "_databaseLoaded", true);
            return provider;
        }

        #endregion

        #region MaxMind — QueryDatabase / GetLocationAsync via injected reader

        [Fact]
        public async Task MaxMind_InjectedReader_InvalidIp_ReturnsNull()
        {
            var provider = NewMaxMindWithReader(new FakeReader(_ => RichResponse()));
            Assert.Null(await provider.GetLocationAsync("not-an-ip"));
        }

        [Fact]
        public async Task MaxMind_InjectedReader_RichResponse_MapsAllFields()
        {
            var provider = NewMaxMindWithReader(new FakeReader(_ => RichResponse()));

            var geo = await provider.GetLocationAsync("8.8.8.8");

            Assert.NotNull(geo);
            Assert.Equal("United States", geo.CountryName);
            Assert.Equal("US", geo.CountryCode);
            Assert.Equal("California", geo.RegionName);
            Assert.Equal("Mountain View", geo.CityName);
            Assert.Equal(37.4, geo.Latitude);
            Assert.Equal(-122.1, geo.Longitude);
        }

        [Fact]
        public async Task MaxMind_InjectedReader_NullCityResponse_ReturnsNull()
        {
            var provider = NewMaxMindWithReader(new FakeReader(_ => null));
            Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
        }

        [Fact]
        public async Task MaxMind_InjectedReader_CityThrows_ReturnsNull()
        {
            var provider = NewMaxMindWithReader(new FakeReader(_ => throw new InvalidOperationException("query-fails")));
            Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
        }

        [Fact]
        public async Task MaxMind_InjectedReader_WithoutCityMethod_ReturnsNull()
        {
            var provider = NewMaxMindWithReader(new object());
            Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
        }

        [Fact]
        public async Task MaxMind_InjectedReader_EmptyResponseObject_ReturnsEmptyGeoInfo()
        {
            // Response with all-null members walks the null branches of QueryDatabase.
            var provider = NewMaxMindWithReader(new FakeReader(_ => new FakeCityResponse
            {
                Subdivisions = new List<FakeSubdivision>()
            }));

            var geo = await provider.GetLocationAsync("8.8.8.8");

            Assert.NotNull(geo);
            Assert.Null(geo.CountryName);
            Assert.Null(geo.CityName);
        }

        [Fact]
        public void MaxMind_QueryDatabase_NullReader_ReturnsNull()
        {
            var provider = NewMaxMind();
            Assert.Null(InvokePrivate(provider, "QueryDatabase", IPAddress.Parse("8.8.8.8")));
        }

        [Fact]
        public async Task MaxMind_ThrowingDebugLogger_OuterCatch_ReturnsNull()
        {
            var provider = new MaxMindOfflineGeoLocationProvider(new ThrowOnDebugLogger<MaxMindOfflineGeoLocationProvider>(), NewDbPath(".mmdb"));
            // Private IP triggers LogDebug which throws -> outer catch -> LogWarning -> null.
            Assert.Null(await provider.GetLocationAsync("10.1.2.3"));
        }

        [Fact]
        public void MaxMind_IsLocalOrPrivateIp_NullAndLoopback()
        {
            var provider = NewMaxMind();
            Assert.True((bool)InvokePrivate(provider, "IsLocalOrPrivateIp", new object[] { null }));
            Assert.True((bool)InvokePrivate(provider, "IsLocalOrPrivateIp", "::1"));
        }

        [Fact]
        public void MaxMind_DoubleCheckedLoadBranch_Covered()
        {
            CoverDoubleCheckedLoadBranch(NewMaxMind());
        }

        #endregion

        #region MaxMind — EnsureDatabaseLoadedAsync with emitted "MaxMind.GeoIP2" assembly

        private static bool MaxMindAssemblyLoaded()
        {
            return AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "MaxMind.GeoIP2");
        }

        private static bool WaitForUnload(WeakReference weakAssembly)
        {
            for (int i = 0; i < 100 && (weakAssembly.IsAlive || MaxMindAssemblyLoaded()); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Sleep(10);
            }

            return !MaxMindAssemblyLoaded();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private WeakReference EmitAssemblyWithoutReaderType_AndExercise()
        {
            var ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("MaxMind.GeoIP2"), AssemblyBuilderAccess.RunAndCollect);
            var mb = ab.DefineDynamicModule("main");
            mb.DefineType("MaxMind.GeoIP2.SomethingElse", TypeAttributes.Public | TypeAttributes.Class).CreateType();

            // Assembly found, DatabaseReader type not found -> reader stays null.
            var provider = NewMaxMind();
            Assert.Null(provider.GetLocationAsync("8.8.8.8").GetAwaiter().GetResult());
            Assert.Null(GetField(provider, "_databaseReader"));

            return new WeakReference(ab);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private WeakReference EmitAssemblyWithoutStringCtor_AndExercise()
        {
            var ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("MaxMind.GeoIP2"), AssemblyBuilderAccess.RunAndCollect);
            var mb = ab.DefineDynamicModule("main");
            // DatabaseReader with only the implicit parameterless constructor.
            mb.DefineType("MaxMind.GeoIP2.DatabaseReader", TypeAttributes.Public | TypeAttributes.Class).CreateType();

            // Type found but ctor(string) missing -> reader stays null.
            var provider = NewMaxMind();
            Assert.Null(provider.GetLocationAsync("8.8.8.8").GetAwaiter().GetResult());
            Assert.Null(GetField(provider, "_databaseReader"));

            return new WeakReference(ab);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EmitFullReaderAssembly()
        {
            var ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("MaxMind.GeoIP2"), AssemblyBuilderAccess.Run);
            var mb = ab.DefineDynamicModule("main");
            var tb = mb.DefineType("MaxMind.GeoIP2.DatabaseReader", TypeAttributes.Public | TypeAttributes.Class);
            var pathField = tb.DefineField("_path", typeof(string), FieldAttributes.Private);

            var ctor = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(string) });
            var il = ctor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes));
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, typeof(MaxMindHook).GetMethod(nameof(MaxMindHook.OnCreate)));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, pathField);
            il.Emit(OpCodes.Ret);

            var city = tb.DefineMethod("City", MethodAttributes.Public, typeof(object), new[] { typeof(IPAddress) });
            il = city.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, pathField);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, typeof(MaxMindHook).GetMethod(nameof(MaxMindHook.OnCity)));
            il.Emit(OpCodes.Ret);

            tb.CreateType();
        }

        /// <summary>
        /// Single sequential test that walks every EnsureDatabaseLoadedAsync branch by
        /// controlling which "MaxMind.GeoIP2" assembly shape is loaded at each step.
        /// </summary>
        [Fact]
        public async Task MaxMind_EnsureDatabaseLoaded_AllAssemblyShapes()
        {
            // Stage A: no MaxMind.GeoIP2 assembly at all -> "assembly not found" branch.
            if (!MaxMindAssemblyLoaded())
            {
                var noAsm = NewMaxMind();
                Assert.Null(await noAsm.GetLocationAsync("8.8.8.8"));
                Assert.Null(GetField(noAsm, "_databaseReader"));
            }

            // Stage B: assembly present but DatabaseReader type missing.
            var weakB = EmitAssemblyWithoutReaderType_AndExercise();
            bool unloadedB = WaitForUnload(weakB);

            // Stage C: DatabaseReader present but no ctor(string).
            bool unloadedC = unloadedB;
            if (unloadedB)
            {
                var weakC = EmitAssemblyWithoutStringCtor_AndExercise();
                unloadedC = WaitForUnload(weakC);
            }

            // Stage D: full reader wired to MaxMindHook (permanent assembly).
            if (unloadedC)
            {
                EmitFullReaderAssembly();

                // D1: constructor throws for an unregistered path -> load catch branch.
                var failing = NewMaxMind();
                Assert.Null(await failing.GetLocationAsync("8.8.8.8"));
                Assert.Null(GetField(failing, "_databaseReader"));

                // D2: registered path -> reader constructed, City() flows end to end.
                var okPath = NewDbPath(".mmdb");
                MaxMindHook.CityHandlers[okPath] = _ => RichResponse();
                var ok = new MaxMindOfflineGeoLocationProvider(NullLogger<MaxMindOfflineGeoLocationProvider>.Instance, okPath);
                var geo = await ok.GetLocationAsync("8.8.8.8");
                Assert.NotNull(geo);
                Assert.Equal("US", geo.CountryCode);
                Assert.NotNull(GetField(ok, "_databaseReader"));
            }

            Assert.True(unloadedB, "collectible MaxMind.GeoIP2 stage-B assembly was not unloaded");
            Assert.True(unloadedC, "collectible MaxMind.GeoIP2 stage-C assembly was not unloaded");
        }

        #endregion

        #region ChunZhen — crafted qqwry.dat binaries

        private ChunZhenOfflineGeoLocationProvider NewChunZhen(byte[] db, ILogger<ChunZhenOfflineGeoLocationProvider> logger = null)
        {
            var path = WriteFile("qqwry-" + Guid.NewGuid().ToString("N") + ".dat", db);
            return new ChunZhenOfflineGeoLocationProvider(logger ?? NullLogger<ChunZhenOfflineGeoLocationProvider>.Instance, path);
        }

        /// <summary>Database with two index records: [0.0.0.0, ...] and [80.0.0.0, ...], both pointing at one string.</summary>
        private static byte[] BuildTwoRecordDb(string location)
        {
            var loc = Encoding.UTF8.GetBytes(location);
            uint indexOffset = (uint)(8 + loc.Length + 1);
            var data = new byte[indexOffset + 14];

            BitConverter.GetBytes(indexOffset).CopyTo(data, 0);
            BitConverter.GetBytes(indexOffset + 14).CopyTo(data, 4);
            loc.CopyTo(data, 8);
            data[8 + loc.Length] = 0;

            // record 0: IP 0.0.0.0 -> string at 8
            data[indexOffset + 4] = 8;
            // record 1: IP 80.0.0.0 (0x50000000 LE) -> string at 8
            data[indexOffset + 7 + 3] = 0x50;
            data[indexOffset + 7 + 4] = 8;

            return data;
        }

        [Fact]
        public async Task ChunZhen_TwoRecords_BinarySearchGoesLeft()
        {
            // 8.8.8.8 < 80.0.0.0 forces the "right = middle" branch.
            var provider = NewChunZhen(BuildTwoRecordDb("China Beijing Haidian"));

            var geo = await provider.GetLocationAsync("8.8.8.8");

            Assert.NotNull(geo);
            Assert.Equal("China", geo.CountryName);
            Assert.Equal("CN", geo.CountryCode);
        }

        [Fact]
        public async Task ChunZhen_HugeIndexOffset_SearchCatch_ReturnsNull()
        {
            // Header points the index at 0xFFFFFFF0, whose (int) cast is negative:
            // data[-16] throws inside the binary search -> catch -> null.
            var data = new byte[16];
            BitConverter.GetBytes(0xFFFFFFF0u).CopyTo(data, 0);
            BitConverter.GetBytes(0xFFFFFFF7u).CopyTo(data, 4);

            var provider = NewChunZhen(data);
            Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
        }

        [Fact]
        public async Task ChunZhen_RedirectByte_FollowsRedirect()
        {
            // Location area at offset 8 starts with 0x01 + 3-byte redirect to offset 12.
            var loc = Encoding.UTF8.GetBytes("China Shanghai");
            uint indexOffset = (uint)(12 + loc.Length + 1);
            var data = new byte[indexOffset + 7];

            BitConverter.GetBytes(indexOffset).CopyTo(data, 0);
            BitConverter.GetBytes(indexOffset + 7).CopyTo(data, 4);
            data[8] = 0x01;
            data[9] = 12; // redirect target (3-byte LE)
            loc.CopyTo(data, 12);
            data[12 + loc.Length] = 0;
            data[indexOffset + 4] = 8; // record 0: IP 0 -> string at 8 (redirected)

            var provider = NewChunZhen(data);
            var geo = await provider.GetLocationAsync("8.8.8.8");

            Assert.NotNull(geo);
            Assert.Equal("China", geo.CountryName);
            Assert.Equal("Shanghai", geo.RegionName);
        }

        [Fact]
        public async Task ChunZhen_LocationPointsAtNulByte_ReturnsNull()
        {
            // The location offset points directly at a 0x00 byte -> empty string -> null.
            uint indexOffset = 9;
            var data = new byte[indexOffset + 7];
            BitConverter.GetBytes(indexOffset).CopyTo(data, 0);
            BitConverter.GetBytes(indexOffset + 7).CopyTo(data, 4);
            data[8] = 0x00;
            data[indexOffset + 4] = 8;

            var provider = NewChunZhen(data);
            Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
        }

        [Fact]
        public async Task ChunZhen_LocationOffsetBeyondFile_ReturnsNull()
        {
            // Record found (left > 0) but its 3-byte location offset points past the
            // end of the file, so the lookup falls through and returns null.
            uint indexOffset = 8;
            var data = new byte[indexOffset + 7];
            BitConverter.GetBytes(indexOffset).CopyTo(data, 0);
            BitConverter.GetBytes(indexOffset + 7).CopyTo(data, 4);
            data[indexOffset + 4] = 0xFF;
            data[indexOffset + 5] = 0xFF;
            data[indexOffset + 6] = 0xFF;

            var provider = NewChunZhen(data);
            Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
        }

        [Fact]
        public async Task ChunZhen_LoadFailure_WhileFileLocked_ReturnsNull()
        {
            var path = WriteFile("locked-" + Guid.NewGuid().ToString("N") + ".dat", BuildTwoRecordDb("China Beijing"));
            var provider = new ChunZhenOfflineGeoLocationProvider(NullLogger<ChunZhenOfflineGeoLocationProvider>.Instance, path);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
            }

            // After the lock is released the provider retries and succeeds.
            var geo = await provider.GetLocationAsync("8.8.8.8");
            Assert.NotNull(geo);
        }

        [Fact]
        public async Task ChunZhen_ThrowingDebugLogger_OuterCatch_ReturnsNull()
        {
            var provider = NewChunZhen(BuildTwoRecordDb("China Beijing"), new ThrowOnDebugLogger<ChunZhenOfflineGeoLocationProvider>());
            Assert.Null(await provider.GetLocationAsync("192.168.0.1"));
        }

        [Fact]
        public void ChunZhen_DoubleCheckedLoadBranch_Covered()
        {
            CoverDoubleCheckedLoadBranch(NewChunZhen(BuildTwoRecordDb("China Beijing")));
        }

        [Fact]
        public void ChunZhen_PrivateHelpers_EdgeCases()
        {
            var provider = NewChunZhen(BuildTwoRecordDb("China Beijing"));
            SetField(provider, "_databaseData", new byte[] { 0x41, 0x00, 0x01 });

            // ReadLocationString: out-of-range offsets.
            Assert.Null(InvokePrivate(provider, "ReadLocationString", -1));
            Assert.Null(InvokePrivate(provider, "ReadLocationString", 100));

            // ReadUInt32 / ReadUInt24: not enough bytes -> 0.
            Assert.Equal(0u, (uint)InvokePrivate(provider, "ReadUInt32", new byte[] { 1, 2 }, 0));
            Assert.Equal(0u, (uint)InvokePrivate(provider, "ReadUInt24", new byte[] { 1 }, 0));

            // ParseLocationString: null -> empty info.
            var geo = (GeoLocationInfo)InvokePrivate(provider, "ParseLocationString", new object[] { null });
            Assert.NotNull(geo);
            Assert.Null(geo.CountryName);

            // GetCountryCode: null -> null.
            Assert.Null(InvokePrivate(provider, "GetCountryCode", new object[] { null }));

            // IsLocalOrPrivateIp: null -> true.
            Assert.True((bool)InvokePrivate(provider, "IsLocalOrPrivateIp", new object[] { null }));
        }

        #endregion

        #region IpIpNet offline — parser helpers via reflection

        private IpIpNetOfflineGeoLocationProvider NewIpIpNet(ILogger<IpIpNetOfflineGeoLocationProvider> logger = null)
        {
            return new IpIpNetOfflineGeoLocationProvider(
                logger ?? NullLogger<IpIpNetOfflineGeoLocationProvider>.Instance,
                NewDbPath(".ipdb"));
        }

        [Fact]
        public async Task IpIpNet_LoopbackIp_ReturnsNull()
        {
            Assert.Null(await NewIpIpNet().GetLocationAsync("127.0.0.1"));
        }

        [Fact]
        public async Task IpIpNet_SecondLookup_TakesLoadedFastPath()
        {
            var provider = NewIpIpNet();
            Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
            Assert.Null(await provider.GetLocationAsync("9.9.9.9"));
        }

        [Fact]
        public async Task IpIpNet_LoadFailure_WhileFileLocked_ReturnsNull()
        {
            var path = NewDbPath(".ipdb");
            var provider = new IpIpNetOfflineGeoLocationProvider(NullLogger<IpIpNetOfflineGeoLocationProvider>.Instance, path);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
            }
        }

        [Fact]
        public async Task IpIpNet_ThrowingDebugLogger_OuterCatch_ReturnsNull()
        {
            var provider = NewIpIpNet(new ThrowOnDebugLogger<IpIpNetOfflineGeoLocationProvider>());
            Assert.Null(await provider.GetLocationAsync("172.16.5.5"));
        }

        [Fact]
        public void IpIpNet_DoubleCheckedLoadBranch_Covered()
        {
            CoverDoubleCheckedLoadBranch(NewIpIpNet());
        }

        [Fact]
        public void IpIpNet_ParseLocationString_AllShapes()
        {
            var provider = NewIpIpNet();

            var empty = (GeoLocationInfo)InvokePrivate(provider, "ParseLocationString", new object[] { null });
            Assert.Null(empty.CountryName);

            var full = (GeoLocationInfo)InvokePrivate(provider, "ParseLocationString", "中国|北京|海淀");
            Assert.Equal("中国", full.CountryName);
            Assert.Equal("CN", full.CountryCode);
            Assert.Equal("北京", full.RegionName);
            Assert.Equal("海淀", full.CityName);

            var one = (GeoLocationInfo)InvokePrivate(provider, "ParseLocationString", "Narnia");
            Assert.Equal("Narnia", one.CountryName);
            Assert.Null(one.CountryCode);
        }

        [Fact]
        public void IpIpNet_GetCountryCode_KnownUnknownAndNull()
        {
            var provider = NewIpIpNet();
            Assert.Null(InvokePrivate(provider, "GetCountryCode", new object[] { null }));
            Assert.Equal("US", InvokePrivate(provider, "GetCountryCode", "United States"));
            Assert.Null(InvokePrivate(provider, "GetCountryCode", "Atlantis"));
        }

        [Fact]
        public void IpIpNet_IsLocalOrPrivateIp_NullAndLoopback()
        {
            var provider = NewIpIpNet();
            Assert.True((bool)InvokePrivate(provider, "IsLocalOrPrivateIp", new object[] { null }));
            Assert.True((bool)InvokePrivate(provider, "IsLocalOrPrivateIp", "::1"));
        }

        #endregion
    }
}
