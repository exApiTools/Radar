using System;
using System.Collections.Generic;
using System.Linq;

namespace Radar;

public static class KMeans
{
    public static int[] Cluster(Vector2d[] rawData, int numClusters)
    {
        if (numClusters >= rawData.Length)
        {
            return Enumerable.Range(0, rawData.Length).ToArray();
        }

        var clusteringUpdated = true;
        var meansUpdated = true;
        var clustering = InitClustering(rawData, numClusters);
        var means = new Vector2d[numClusters];
        var num = rawData.Length * 10;
        var index = 0;
        for (; clusteringUpdated && meansUpdated && index < num; ++index)
        {
            meansUpdated = UpdateMeans(rawData, clustering, means);
            clusteringUpdated = UpdateClustering(rawData, clustering, means);
        }

        return clustering;
    }

    private static int[] InitClustering(Vector2d[] data, int numClusters)
    {
        var selectedClusters = new HashSet<int> { 0 };
        while (selectedClusters.Count < numClusters)
        {
            var newPointIndex = data.Select((tuple, index) => (tuple, index))
               .Where(x => !selectedClusters.Contains(x.index))
               .MaxBy(c => selectedClusters.Min(x => Distance(c.tuple, data[x])));
            selectedClusters.Add(newPointIndex.index);
        }

        var clusterNumbers = selectedClusters.Select((x, i) => (x, i)).ToDictionary(x => x.x, x => x.i);
        var numArray = data.Select(x => clusterNumbers[selectedClusters.MinBy(y => Distance(x, data[y]))]).ToArray();
        return numArray;
    }

    private static bool UpdateMeans(Vector2d[] data, int[] clustering, Vector2d[] means)
    {
        var length = means.Length;
        var numArray = new int[length];
        for (var index1 = 0; index1 < data.Length; ++index1)
        {
            var index2 = clustering[index1];
            ++numArray[index2];
        }

        for (var i = 0; i < length; ++i)
        {
            if (numArray[i] == 0)
                return false;
        }

        for (var i = 0; i < means.Length; i++)
        {
            means[i] = default;
        }

        for (var i = 0; i < data.Length; ++i)
        {
            means[clustering[i]] += data[i];
        }

        for (var i = 0; i < means.Length; ++i)
        {
            means[i] /= numArray[i];
        }

        return true;
    }

    private static bool UpdateClustering(Vector2d[] data, int[] clustering, Vector2d[] means)
    {
        var length = means.Length;
        var didUpdate = false;
        var clusteringCopy = new int[clustering.Length];
        Array.Copy(clustering, clusteringCopy, clustering.Length);
        var clusterSizes = clusteringCopy.GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
        var distances = new double[length];
        for (var index1 = 0; index1 < data.Length; ++index1)
        {
            for (var index2 = 0; index2 < length; ++index2)
                distances[index2] = Distance(data[index1], means[index2]);
            var newClusterIndex = distances.Select((distance, index) => (distance, index)).MinBy(x => x.distance).index;
            ref var clusterIndex = ref clusteringCopy[index1];
            if (newClusterIndex != clusterIndex)
            {
                didUpdate = true;
                if (clusterSizes[clusterIndex] > 1)
                {
                    clusterSizes[clusterIndex]--;
                    clusterSizes[newClusterIndex]++;
                    clusterIndex = newClusterIndex;
                }
            }
        }

        if (!didUpdate)
            return false;
        var numArray2 = new int[length];
        for (var index3 = 0; index3 < data.Length; ++index3)
        {
            var index4 = clusteringCopy[index3];
            ++numArray2[index4];
        }

        for (var index = 0; index < length; ++index)
        {
            if (numArray2[index] == 0)
                return false;
        }

        Array.Copy(clusteringCopy, clustering, clusteringCopy.Length);
        return true;
    }

    private static double Distance(Vector2d v1, Vector2d v2)
    {
        return (v1 - v2).Length;
    }
}
