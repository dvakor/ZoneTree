using Tenray.ZoneTree.WAL;

namespace Tenray.ZoneTree.UnitTests
{
    public class Crc32ComputerTests
    {
        [Test]
        public void Crc32UintUlongTest()
        {
            var result = Crc32Computer.Compute(0, (ulong)123456789);
            Assert.That(result, Is.EqualTo(3531890030));
        }
        
        [Test]
        public void Crc32UintUintTest()
        {
            var result = Crc32Computer.Compute(0, (uint)123456789);
            Assert.That(result, Is.EqualTo(3177508098));
        }
        
        [Test]
        public void Crc32UintIntTest()
        {
            var result = Crc32Computer.Compute(0, 123456789);
            Assert.That(result, Is.EqualTo(3177508098));
        }
        
        [Test]
        public void Crc32UintBytesTest()
        {
            var result = Crc32Computer.Compute(0, new byte[]{0x1, 0x2, 0x3, 0x4, 0x5});
            Assert.That(result, Is.EqualTo(39845830));
        }
    }
}