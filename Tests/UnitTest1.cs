using GameOffsets.Native;
using Radar;

namespace Tests
{
    public class Tests
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void Test1()
        {
            var pf = new JpsPathFinder(new int[][] { new int[] { 0, 0 } }, new []{0});
            Assert.NotNull(pf.RunFirstScan(new Vector2i(0, 0), new Vector2i(1, 0)).Last());
        }
        [Test]
        public void Test2()
        {
            var pf = new JpsPathFinder(new int[][] { 
                new int[] { 0, 0,0 },
                new int[] { 0, 1,1 },
                new int[] { 0, 1,1 },
            }, new[] { 0 });
            var path = pf.RunFirstScan(new Vector2i(2, 0), new Vector2i(0, 2)).Last();
            Console.WriteLine(string.Join(" ",path));
            Assert.NotNull(path);
        }
        [Test]
        public void Test3()
        {
            var pf = new JpsPathFinder(new int[][] {
                new int[] { 0, 0, },
                new int[] { 0, 1, },
            }, new[] { 0 });
            var path = pf.RunFirstScan(new Vector2i(1, 0), new Vector2i(0, 1)).Last();
            Console.WriteLine(string.Join(" ", path));
            Assert.NotNull(path);
        }
    }
}