using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class ParseAmountTests
    {
        // OCR of the resource Overview often drops the decimal point: "10.3 M" comes back as "103M", "1.0 M" as "10M"
        [TestCase("154.4 M", 154.4e6)]
        [TestCase("103M", 10.3e6)]
        [TestCase("10M", 1.0e6)]
        [TestCase("421.5 K", 421.5e3)]
        [TestCase("521", 521.0)]
        public void ParsesTheGamesAbbreviatedAmounts(string text, double expected) =>
            Assert.That(FarmingTask.ParseAmount(text), Is.EqualTo(expected).Within(1e-6 * expected));

        [Test]
        public void ReturnsNullForText() => Assert.That(FarmingTask.ParseAmount("Owns"), Is.Null);
    }
}
