using System.Text;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;

namespace Cyaim.WebSocketServer.Tests
{
    public class FindJsonPropertyValueTests
    {
        private readonly MvcChannelHandler _handler = new MvcChannelHandler();

        private string Find(string json)
            => _handler.FindJsonPropertyValue(Encoding.UTF8.GetBytes(json));

        private string Find(string json, string propertyName)
            => _handler.FindJsonPropertyValue(Encoding.UTF8.GetBytes(json), propertyName);

        [Fact]
        public void ExtractsTargetFromCompleteJson()
        {
            Assert.Equal("chat.send", Find("{\"id\":\"1\",\"target\":\"chat.send\",\"body\":{}}"));
        }

        [Fact]
        public void ExtractsTarget_WhenTargetIsFirstProperty()
        {
            Assert.Equal("a.b", Find("{\"target\":\"a.b\"}"));
        }

        [Theory]
        [InlineData("{\"Target\":\"x.y\"}")]
        [InlineData("{\"TARGET\":\"x.y\"}")]
        [InlineData("{\"TaRgEt\":\"x.y\"}")]
        public void PropertyNameMatch_IsCaseInsensitive(string json)
        {
            Assert.Equal("x.y", Find(json));
        }

        [Fact]
        public void MissingProperty_ReturnsNull()
        {
            Assert.Null(Find("{\"id\":\"1\",\"body\":{}}"));
        }

        [Fact]
        public void NonStringTargetValue_ReturnsNull()
        {
            Assert.Null(Find("{\"target\":123}"));
        }

        [Theory]
        [InlineData("{\"targ")]
        [InlineData("{")]
        [InlineData("not json at all")]
        [InlineData("[1,2,")]
        [InlineData("")]
        public void InvalidOrPartialJson_ReturnsNull_DoesNotThrow(string fragment)
        {
            Assert.Null(Find(fragment));
        }

        [Fact]
        public void PartialJson_TargetAlreadyReceived_IsFound()
        {
            // Truncated fragment: target property is complete even though the document is not
            Assert.Equal("foo.bar", Find("{\"id\":\"1\",\"target\":\"foo.bar\",\"body\":{\"a\":"));
        }

        [Fact]
        public void PartialJson_TargetValueTruncated_ReturnsNull()
        {
            Assert.Null(Find("{\"id\":\"1\",\"target\":\"foo.b"));
        }

        [Fact]
        public void CustomPropertyName_IsUsed()
        {
            Assert.Equal("42", Find("{\"id\":\"42\",\"target\":\"x\"}", "id"));
        }

        [Fact]
        public void CustomPropertyName_CaseInsensitive()
        {
            Assert.Equal("42", Find("{\"ID\":\"42\"}", "id"));
        }

        [Fact]
        public void EmptySpan_ReturnsNull()
        {
            Assert.Null(_handler.FindJsonPropertyValue(ReadOnlySpan<byte>.Empty));
        }

        [Fact]
        public void FirstMatchingProperty_Wins()
        {
            Assert.Equal("first", Find("{\"target\":\"first\",\"other\":{\"target\":\"second\"}}"));
        }
    }
}
