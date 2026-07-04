using System.Text.Json.Nodes;
using Cyaim.WebSocketServer.Infrastructure.Handlers;

namespace Cyaim.WebSocketServer.Tests
{
    public class DataTypesTests
    {
        private class Poco
        {
            public string Name { get; set; }
            public int Age { get; set; }
        }

        #region ConvertTo(Type, JsonNode)

        public static IEnumerable<object[]> JsonNodeConversions => new List<object[]>
        {
            new object[] { typeof(string), "\"hello\"", "hello" },
            new object[] { typeof(sbyte), "-5", (sbyte)-5 },
            new object[] { typeof(byte), "200", (byte)200 },
            new object[] { typeof(short), "-1234", (short)-1234 },
            new object[] { typeof(ushort), "60000", (ushort)60000 },
            new object[] { typeof(int), "-123456", -123456 },
            new object[] { typeof(uint), "3000000000", 3000000000u },
            new object[] { typeof(long), "-9223372036854775808", long.MinValue },
            new object[] { typeof(ulong), "18446744073709551615", ulong.MaxValue },
            new object[] { typeof(float), "1.5", 1.5f },
            new object[] { typeof(double), "3.25", 3.25d },
            new object[] { typeof(bool), "true", true },
            new object[] { typeof(bool), "false", false },
            new object[] { typeof(char), "\"a\"", 'a' },
            new object[] { typeof(decimal), "123.45", 123.45m },
            new object[] { typeof(decimal), "79228162514264337593543950335", decimal.MaxValue },
        };

        [Theory]
        [MemberData(nameof(JsonNodeConversions))]
        public void ConvertTo_JsonNode_ConvertsBasicTypes(Type target, string json, object expected)
        {
            JsonNode node = JsonNode.Parse(json);

            object result = target.ConvertTo(node);

            Assert.IsType(target, result);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void ConvertTo_JsonNode_DateTime()
        {
            JsonNode node = JsonNode.Parse("\"2024-05-06T07:08:09\"");

            object result = typeof(DateTime).ConvertTo(node);

            Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9), result);
        }

        [Fact]
        public void ConvertTo_JsonNode_DateTimeOffset()
        {
            JsonNode node = JsonNode.Parse("\"2024-05-06T07:08:09+02:00\"");

            object result = typeof(DateTimeOffset).ConvertTo(node);

            Assert.Equal(new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.FromHours(2)), result);
        }

        [Fact]
        public void ConvertTo_JsonNode_FallbackDeserializesComplexType()
        {
            JsonNode node = JsonNode.Parse("{\"Name\":\"neo\",\"Age\":42}");

            object result = typeof(Poco).ConvertTo(node);

            Poco poco = Assert.IsType<Poco>(result);
            Assert.Equal("neo", poco.Name);
            Assert.Equal(42, poco.Age);
        }

        [Fact]
        public void ConvertTo_JsonNode_FallbackDeserializesArray()
        {
            JsonNode node = JsonNode.Parse("[1,2,3]");

            object result = typeof(int[]).ConvertTo(node);

            Assert.Equal(new[] { 1, 2, 3 }, Assert.IsType<int[]>(result));
        }

        [Fact]
        public void ConvertTo_JsonNode_InvalidConversion_Propagates()
        {
            JsonNode node = JsonNode.Parse("\"not-a-number\"");

            Assert.ThrowsAny<Exception>(() => typeof(int).ConvertTo(node));
        }

        #endregion

        #region ConvertTo(Type, object)

        public static IEnumerable<object[]> ObjectConversions => new List<object[]>
        {
            new object[] { typeof(string), 42, "42" },
            new object[] { typeof(sbyte), "-5", (sbyte)-5 },
            new object[] { typeof(byte), "200", (byte)200 },
            new object[] { typeof(short), "-1234", (short)-1234 },
            new object[] { typeof(ushort), "60000", (ushort)60000 },
            new object[] { typeof(int), "42", 42 },
            new object[] { typeof(int), 42L, 42 },
            new object[] { typeof(uint), "3000000000", 3000000000u },
            new object[] { typeof(long), "-42", -42L },
            new object[] { typeof(ulong), "18446744073709551615", ulong.MaxValue },
            new object[] { typeof(float), 1.5d, 1.5f },
            new object[] { typeof(double), 3.25f, 3.25d },
            new object[] { typeof(bool), "true", true },
            new object[] { typeof(bool), 1, true },
            new object[] { typeof(char), "a", 'a' },
            // decimal was recently fixed to use Convert.ToDecimal
            new object[] { typeof(decimal), 123.45d, 123.45m },
            new object[] { typeof(decimal), "123.45", 123.45m },
            new object[] { typeof(decimal), 42, 42m },
        };

        [Theory]
        [MemberData(nameof(ObjectConversions))]
        public void ConvertTo_Object_ConvertsBasicTypes(Type target, object value, object expected)
        {
            object result = target.ConvertTo(value);

            Assert.IsType(target, result);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void ConvertTo_Object_Decimal_UsesConvertToDecimal_KeepsPrecision()
        {
            object result = typeof(decimal).ConvertTo((object)"79228162514264337593543950335");

            Assert.Equal(decimal.MaxValue, result);
        }

        [Fact]
        public void ConvertTo_Object_DateTime()
        {
            object result = typeof(DateTime).ConvertTo((object)"2024-05-06T07:08:09");

            Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9), result);
        }

        [Fact]
        public void ConvertTo_Object_DateTimeOffset()
        {
            object result = typeof(DateTimeOffset).ConvertTo((object)"2024-05-06T07:08:09+02:00");

            Assert.Equal(new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.FromHours(2)), result);
        }

        [Fact]
        public void ConvertTo_Object_FallbackChangeType_SameTypeInstance_ReturnsValue()
        {
            var poco = new Poco { Name = "x" };

            object result = typeof(Poco).ConvertTo((object)poco);

            Assert.Same(poco, result);
        }

        [Fact]
        public void ConvertTo_Object_FallbackChangeType_EnumTarget_NotSupported()
        {
            // Enum targets hit the Convert.ChangeType fallback, which does not support
            // int -> enum conversion and throws InvalidCastException (current behavior).
            Assert.Throws<InvalidCastException>(() => typeof(DayOfWeek).ConvertTo((object)3));
        }

        [Fact]
        public void ConvertTo_Object_InvalidConversion_Propagates()
        {
            Assert.ThrowsAny<Exception>(() => typeof(int).ConvertTo((object)"abc"));
        }

        [Fact]
        public void ConvertTo_Object_JsonNodeTargets_ParseFromString()
        {
            // The Type_JsonNode/Type_JsonObject/Type_JsonArray/Type_JsonValue constants now match the
            // real FullName "System.Text.Json.Nodes.*", so string values are parsed into JSON nodes.
            Assert.Equal("System.Text.Json.Nodes.JsonNode", typeof(JsonNode).FullName);

            var node = typeof(JsonNode).ConvertTo((object)"{\"a\":1}");
            Assert.IsAssignableFrom<JsonNode>(node);
            Assert.Equal(1, ((JsonNode)node)["a"].GetValue<int>());

            var obj = typeof(JsonObject).ConvertTo((object)"{\"a\":1}");
            Assert.IsAssignableFrom<JsonNode>(obj);

            var arr = typeof(JsonArray).ConvertTo((object)"[1]");
            Assert.IsAssignableFrom<JsonNode>(arr);
        }

        #endregion

        #region IsBasicType

        [Theory]
        [InlineData(typeof(string), true)]
        [InlineData(typeof(sbyte), true)]
        [InlineData(typeof(byte), true)]
        [InlineData(typeof(short), true)]
        [InlineData(typeof(ushort), true)]
        [InlineData(typeof(int), true)]
        [InlineData(typeof(uint), true)]
        [InlineData(typeof(long), true)]
        [InlineData(typeof(ulong), true)]
        [InlineData(typeof(float), true)]
        [InlineData(typeof(double), true)]
        [InlineData(typeof(bool), true)]
        [InlineData(typeof(char), true)]
        [InlineData(typeof(decimal), true)]
        // DateTime / DateTimeOffset are deliberately excluded (commented out in the library)
        [InlineData(typeof(DateTime), false)]
        [InlineData(typeof(DateTimeOffset), false)]
        [InlineData(typeof(object), false)]
        [InlineData(typeof(byte[]), false)]
        [InlineData(typeof(JsonNode), false)]
        [InlineData(typeof(Task), false)]
        public void IsBasicType_ReturnsExpected(Type type, bool expected)
        {
            Assert.Equal(expected, type.IsBasicType());
        }

        #endregion

        #region IsTaskType

        [Theory]
        [InlineData(typeof(Task), true)]
        [InlineData(typeof(Task<int>), true)]
        [InlineData(typeof(Task<string>), true)]
        [InlineData(typeof(ValueTask), false)]
        [InlineData(typeof(ValueTask<int>), false)]
        [InlineData(typeof(int), false)]
        [InlineData(typeof(List<int>), false)]
        [InlineData(typeof(object), false)]
        public void IsTaskType_ReturnsExpected(Type type, bool expected)
        {
            Assert.Equal(expected, DataTypes.IsTaskType(type));
        }

        [Fact]
        public void IsTaskType_OpenGenericTaskDefinition_IsTask()
        {
            Assert.True(DataTypes.IsTaskType(typeof(Task<>)));
        }

        #endregion
    }
}
