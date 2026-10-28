using System.Text.Json;
using System.Threading.Tasks;
using AssetsManager.Services.Formatting;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Formatting
{
    public class JsonFormatterServiceTests
    {
        [Theory]
        [InlineData("FTW\\u00A0True Damage Album Cover Icon", "FTW\u00A0True Damage Album Cover Icon")]
        [InlineData("FTW\\u00a0HEARTSTEEL Album Cover Icon", "FTW\u00A0HEARTSTEEL Album Cover Icon")]
        [InlineData("FTW\u00A0HEARTSTEEL Album Cover Icon", "FTW\u00A0HEARTSTEEL Album Cover Icon")]
        public async Task FormatJsonAsync_DisplaysNonBreakingSpaces(string title, string expected)
        {
            string json = "{\"title\":\"" + title + "\"}";
            string formatted = await new JsonFormatterService().FormatJsonAsync(json);

            Assert.Contains(expected, formatted);
            using var document = JsonDocument.Parse(formatted);
            Assert.Equal(expected, document.RootElement.GetProperty("title").GetString());
        }

        [Theory]
        [InlineData("Literal \\u00A0")]
        [InlineData("Literal \\\\u00A0")]
        [InlineData("Backslash \\\u00A0 and \"quotes\"\nnext line\twith a tab")]
        public async Task FormatJsonAsync_PreservesEscapesAndStringValues(string value)
        {
            string source = JsonSerializer.Serialize(new { title = value });
            var formatter = new JsonFormatterService();

            foreach (object input in new object[] { source, new { title = value } })
            {
                string formatted = await formatter.FormatJsonAsync(input);
                using var document = JsonDocument.Parse(formatted);
                Assert.Equal(value, document.RootElement.GetProperty("title").GetString());
            }
        }

        [Fact]
        public async Task FormatJsonAsync_PreservesUnicodeInPropertyNamesAndValues()
        {
            const string value = "Espa\u00F1ol \u4E2D\u6587 \uD83D\uDE00\u00A0\u2007\u202F";
            string json = JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, string> { [value] = value });
            var formatter = new JsonFormatterService();
            string formatted = await formatter.FormatJsonAsync(json);

            Assert.Contains("\u00A0\u2007\u202F", formatted);
            Assert.Equal(formatted, await formatter.FormatJsonAsync(formatted));
            using var document = JsonDocument.Parse(formatted);
            Assert.Equal(value, document.RootElement.GetProperty(value).GetString());
        }

        [Fact]
        public async Task FormatJsonAsync_PreservesNumbersAndDuplicateProperties()
        {
            const string source = "{\"n\":123456789012345678901234567890.123456789,\"n\":1.234567890123456789e+123,\"values\":[true,false,null,[],{}]}";
            string formatted = await new JsonFormatterService().FormatJsonAsync(source);
            using var document = JsonDocument.Parse(formatted);
            using var original = JsonDocument.Parse(source);

            Assert.Equal(source, JsonSerializer.Serialize(document));
            Assert.Equal(original.RootElement.GetProperty("n").GetRawText(), document.RootElement.GetProperty("n").GetRawText());
        }

        [Theory]
        [InlineData("null")]
        [InlineData("true")]
        [InlineData("12345678901234567890")]
        [InlineData("\"text\"")]
        [InlineData("[1,2,3]")]
        [InlineData("{ invalid json }")]
        public async Task FormatJsonAsync_HandlesRootValuesAndInvalidInput(string source)
        {
            var formatter = new JsonFormatterService();
            string formatted = await formatter.FormatJsonAsync(source);
            Assert.Equal(formatted, await formatter.FormatJsonAsync(formatted));
            if (source == "{ invalid json }")
                Assert.Equal(source, formatted);
            else
            {
                using var document = JsonDocument.Parse(formatted);
                Assert.Equal(source, JsonSerializer.Serialize(document));
            }
        }

        [Fact]
        public async Task FormatJsonAsync_RespectsReusableSerializerOptions()
        {
            var options = new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
            var input = new { title = "FTW\u00A0HEARTSTEEL", absent = (string)null };
            JsonSerializer.Serialize(input, options);
            var formatter = new JsonFormatterService();
            string formatted = await formatter.FormatJsonAsync(input, options);

            Assert.Contains(input.title, formatted);
            using var document = JsonDocument.Parse(formatted);
            Assert.False(document.RootElement.TryGetProperty("absent", out _));
            Assert.False(options.WriteIndented);
            Assert.Equal(formatted, await formatter.FormatJsonAsync(input, options));
        }

        [Fact]
        public async Task FormatJsonAsync_DisplaysAllUnicodeSpaceSeparators()
        {
            var formatter = new JsonFormatterService();
            for (int codePoint = 0; codePoint <= char.MaxValue; codePoint++)
            {
                char character = (char)codePoint;
                if (char.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.SpaceSeparator)
                    continue;

                string source = JsonSerializer.Serialize(new { title = "before" + character + "after" });
                string formatted = await formatter.FormatJsonAsync(source);
                Assert.Contains("before" + character + "after", formatted);
                using var document = JsonDocument.Parse(formatted);
                Assert.Equal("before" + character + "after", document.RootElement.GetProperty("title").GetString());
            }
        }
    }
}
