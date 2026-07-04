using System.Text.Json;
using System.Text.Json.Nodes;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;

namespace Cyaim.WebSocketServer.Tests
{
    public class MvcSchemeTests
    {
        private static readonly JsonSerializerOptions CaseInsensitive = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        [Fact]
        public void MvcRequestScheme_RoundTrip_PreservesFields()
        {
            var request = new MvcRequestScheme
            {
                Id = "req-1",
                Target = "chat.send",
                Body = new { Message = "hi" }
            };

            string json = JsonSerializer.Serialize(request);
            var back = JsonSerializer.Deserialize<MvcRequestScheme>(json, CaseInsensitive);

            Assert.Equal("req-1", back.Id);
            Assert.Equal("chat.send", back.Target);
            Assert.NotNull(back.Body);
        }

        [Fact]
        public void MvcRequestScheme_Deserialize_LowercaseProperties_CaseInsensitive()
        {
            const string json = "{\"id\":\"7\",\"target\":\"a.b\",\"body\":{\"x\":1}}";

            var request = JsonSerializer.Deserialize<MvcRequestScheme>(json, CaseInsensitive);

            Assert.Equal("7", request.Id);
            Assert.Equal("a.b", request.Target);
            Assert.NotNull(request.Body);
        }

        [Fact]
        public void MvcRequestScheme_BodyNames_ContainsBothCasings()
        {
            Assert.Equal(new[] { "Body", "body" }, MvcRequestScheme.BODY_NAMES);
        }

        [Fact]
        public void MvcRequestScheme_BodyNames_MatchDocumentProperties()
        {
            // Mirrors how MvcChannelHandler extracts the body from a raw request document
            foreach (var (json, expectFound) in new[]
            {
                ("{\"target\":\"t\",\"Body\":{\"a\":1}}", true),
                ("{\"target\":\"t\",\"body\":{\"a\":1}}", true),
                ("{\"target\":\"t\"}", false),
            })
            {
                using var doc = JsonDocument.Parse(json);
                bool hasBody = false;
                JsonElement body = default;
                foreach (string name in MvcRequestScheme.BODY_NAMES)
                {
                    hasBody = doc.RootElement.TryGetProperty(name, out body);
                    if (hasBody) break;
                }
                Assert.Equal(expectFound, hasBody);
                if (expectFound)
                {
                    Assert.Equal(1, JsonObject.Create(body.Clone())["a"].GetValue<int>());
                }
            }
        }

        [Fact]
        public void MvcRequestScheme_BodyNames_IsJsonIgnored()
        {
            string json = JsonSerializer.Serialize(new MvcRequestScheme { Id = "1", Target = "t" });

            Assert.DoesNotContain("BODY_NAMES", json);
        }

        [Fact]
        public void MvcResponseScheme_RoundTrip_PreservesFields()
        {
            long now = DateTime.UtcNow.Ticks;
            var response = new MvcResponseScheme
            {
                Status = 2,
                Msg = "not found",
                Id = "req-1",
                Target = "chat.send",
                RequestTime = now,
                CompleteTime = now + 100,
                Body = new { Answer = 42 }
            };

            string json = JsonSerializer.Serialize(response);
            var back = JsonSerializer.Deserialize<MvcResponseScheme>(json, CaseInsensitive);

            Assert.Equal(2, back.Status);
            Assert.Equal("not found", back.Msg);
            Assert.Equal("req-1", back.Id);
            Assert.Equal("chat.send", back.Target);
            Assert.Equal(now, back.RequestTime);
            Assert.Equal(now + 100, back.CompleteTime);
            Assert.NotNull(back.Body);
        }

        [Fact]
        public void MvcResponseScheme_DefaultStatus_IsZeroSuccess()
        {
            var response = new MvcResponseScheme();

            Assert.Equal(0, response.Status);
            Assert.Null(response.Id);
            Assert.Null(response.Target);
            Assert.Null(response.Body);
        }

        [Fact]
        public void IMvcScheme_TargetConstant_IsLowercaseTarget()
        {
            Assert.Equal("target", IMvcScheme.VAR_TATGET);
        }

        [Fact]
        public void MvcResponseSchemeException_Message_MappedToMsg()
        {
            var ex = new MvcResponseSchemeException("boom") { Status = 1, Id = "i", Target = "t" };

            Assert.Equal("boom", ex.Msg);
            Assert.Equal("boom", ex.Message);
            Assert.Equal(1, ex.Status);
        }
    }
}
