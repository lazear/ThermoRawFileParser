using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ThermoFisher.CommonCore.Data;

namespace ThermoRawFileParser.Util
{
    struct MZArray
    {
        public double[] Masses { get; set; }
        public double[] Intensities { get; set; }
    }

    struct MZData
    {
        public double? basePeakMass;
        public double? basePeakIntensity;
        public double[] masses;
        public double[] intensities;
        public double[] charges;
        public double[] baselineData;
        public double[] noiseData;
        public double[] massData;
        public bool isCentroided;
    }

    /// <summary>
    /// Iterates over numbers based on a provided interval string.
    /// The interval string can specify individual scans or ranges (e.g., "1-5,8,10-12").
    /// Open ended intervals are allowed (e.g., "5-" or "-3"). Inttervals be in ascending order and cannot overlap.
    /// If the interval string is empty, the iterator covers the full range from min to max.
    /// Throws exceptions for invalid formats or edge cases.
    /// </summary>
    public class NumberIterator
    {
        private List<int> edges;
        private readonly Regex valid = new Regex(@"^[\d,\-\s]+$");
        private readonly Regex interval = new Regex(@"^\s*(\d+)?\s*(-)?\s*(\d+)?\s*$");

        public NumberIterator() //empty constructor initializes with no edges
        {
            edges = new List<int>();
        }

        public NumberIterator(string intervalString, int min, int max)
        {
            if (intervalString.IsNullOrEmpty())
            {
                edges = new List<int> { min, max };
            }
            else
            {
                if (!valid.IsMatch(intervalString))
                {
                    throw new Exception("Invalid iterval format.");
                }

                edges = new List<int>();
                var intervals = intervalString.Split(',', StringSplitOptions.TrimEntries);

                foreach (var piece in intervals)
                {
                    try
                    {
                        int start;
                        int end;

                        var intervalMatch = interval.Match(piece);

                        if (!intervalMatch.Success)
                            throw new Exception();

                        if (intervalMatch.Groups[2].Success) //it is interval
                        {
                            if (intervalMatch.Groups[1].Success)
                                start = Math.Max(min, int.Parse(intervalMatch.Groups[1].Value));
                            else
                                start = min; // if no start is specified, use min

                            if (intervalMatch.Groups[3].Success)
                                end = Math.Min(max, int.Parse(intervalMatch.Groups[3].Value));
                            else
                                end = max; // if no end is specified, use max
                        }
                        else
                        {
                            if (intervalMatch.Groups[1].Success)
                                end = start = int.Parse(intervalMatch.Groups[1].Value);
                            else
                                throw new Exception();

                            if (intervalMatch.Groups[3].Success)
                                throw new Exception();
                        }

                        if (edges.Count > 0 && start <= edges[edges.Count - 1])
                        {
                            throw new Exception("Interval edges should be consequtive");
                        }

                        edges.Add(start);
                        edges.Add(end);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"Cannot parse '{piece}' in {intervalString} - {ex.Message}");
                    }
                }
            }

            if (edges.Count == 0)
            {
                throw new Exception("No valid scan numbers provided in the interval string.");
            }
            else if (edges.Count % 2 != 0)
            {
                throw new Exception("Odd number of edges");
            }
        }
        public IEnumerable<int> IterateScans()
        {
            for (int i = 0; i < edges.Count; i += 2)
            {
                for (int scan = edges[i]; scan <= edges[i + 1]; scan++)
                {
                    yield return scan;
                }
            }
        }
    }
}
