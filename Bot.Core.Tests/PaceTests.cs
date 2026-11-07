using Bot.Core.Utils;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    public class PaceTests
    {
        [Test]
        public void ScalesShortWaitsOnlyAndKeepsAFloor()
        {
            Assert.That(Pace.Ms(1000), Is.EqualTo((int)(1000 * Pace.Scale)));
            Assert.That(Pace.Ms(5000), Is.EqualTo(5000));
            Assert.That(Pace.Ms(50), Is.EqualTo(100));
        }
    }
}
