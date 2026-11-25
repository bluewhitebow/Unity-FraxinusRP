using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    internal struct Sorting
    {
        public static ProfilingSampler quickSortSampler = new ProfilingSampler("QuickSort");
        public static ProfilingSampler insertionSortSampler = new ProfilingSampler("InsertionSort");

        public static void QuickSort<T>(T[] data, Func<T, T, int> compare)
        {
            using var scope = new ProfilingScope(quickSortSampler);
            QuickSort<T>(data, 0, data.Length - 1, compare);
        }

        public static void QuickSort<T>(T[] data, int start, int end, Func<T, T, int> compare)
        {
            int diff = end - start;
            if (diff < 1)
                return;
            if (diff < 8)
            {
                InsertionSort(data, start, end, compare);
                return;
            }

            Debug.Assert((uint)start < data.Length);
            Debug.Assert((uint)end < data.Length); // end == inclusive

            if (start < end)
            {
                int pivot = Partition<T>(data, start, end, compare);

                if (pivot >= 1)
                    QuickSort<T>(data, start, pivot, compare);

                if (pivot + 1 < end)
                    QuickSort<T>(data, pivot + 1, end, compare);
            }
        }

        static T Median3Pivot<T>(T[] data, int start, int pivot, int end, Func<T, T, int> compare)
        {
            void Swap(int a, int b)
            {
                var tmp = data[a];
                data[a] = data[b];
                data[b] = tmp;
            }

            if (compare(data[end], data[start]) < 0) Swap(start, end);
            if (compare(data[pivot], data[start]) < 0) Swap(start, pivot);
            if (compare(data[end], data[pivot]) < 0) Swap(pivot, end);
            return data[pivot];
        }

        static int Partition<T>(T[] data, int start, int end, Func<T, T, int> compare)
        {
            int diff = end - start;
            int pivot = start + diff / 2;

            var pivotValue = Median3Pivot(data, start, pivot, end, compare);

            while (true)
            {
                while (compare(data[start], pivotValue) < 0) ++start;
                while (compare(data[end], pivotValue) > 0) --end;

                if (start >= end)
                {
                    return end;
                }

                var tmp = data[start];
                data[start++] = data[end];
                data[end--] = tmp;
            }
        }

        static public void InsertionSort<T>(T[] data, Func<T, T, int> compare)
        {
            using var scope = new ProfilingScope(insertionSortSampler);
            InsertionSort<T>(data, 0, data.Length - 1, compare);
        }

        static public void InsertionSort<T>(T[] data, int start, int end, Func<T, T, int> compare)
        {
            Debug.Assert((uint)start < data.Length);
            Debug.Assert((uint)end < data.Length);

            for (int i = start + 1; i < end + 1; i++)
            {
                var iData = data[i];
                int j = i - 1;
                while (j >= 0 && compare(iData, data[j]) < 0)
                {
                    data[j + 1] = data[j];
                    j--;
                }
                data[j + 1] = iData;
            }
        }
    }
}
