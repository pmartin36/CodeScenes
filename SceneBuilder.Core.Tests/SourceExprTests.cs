using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class SourceExprTests
    {
        [Fact]
        public void StringLiteral_QuotesPlainValue()
        {
            Assert.Equal("\"Enemy\"", SourceExpr.StringLiteral("Enemy"));
        }

        [Fact]
        public void StringLiteral_EscapesQuoteAndBackslash()
        {
            Assert.Equal("\"a\\\"b\\\\c\"", SourceExpr.StringLiteral("a\"b\\c"));
        }

        [Fact]
        public void StringLiteral_EmptyString_ProducesEmptyQuotedLiteral()
        {
            Assert.Equal("\"\"", SourceExpr.StringLiteral(""));
        }

        [Fact]
        public void IntLiteral_PositiveValue_IsBareDigits()
        {
            Assert.Equal("6", SourceExpr.IntLiteral(6));
        }

        [Fact]
        public void IntLiteral_Zero_IsBareZero()
        {
            Assert.Equal("0", SourceExpr.IntLiteral(0));
        }

        [Fact]
        public void IntLiteral_Negative_IncludesMinusSign()
        {
            Assert.Equal("-1", SourceExpr.IntLiteral(-1));
        }
    }
}
