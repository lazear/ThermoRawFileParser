using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ThermoRawFileParser.Query;

namespace ThermoRawFileParserTest
{
    [TestFixture]
    public class QueryTests
    {
        [Test]
        public void TestProxiReaderScans()
        {
            var testRawFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"Data/small.RAW");
            
            var parameters = new QueryParameters
            {
                rawFilePath = testRawFile,
            };

            //Interval of scans to retrieve
            parameters.scans = "1-10";
            ProxiSpectrumReader reader = new ProxiSpectrumReader(parameters);
            var results = reader.Retrieve();
            Assert.That(GetScanNumbers(results), Is.EqualTo(new List<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }));

            //Open-ended interval
            parameters.scans = "-5";
            reader = new ProxiSpectrumReader(parameters);
            results = reader.Retrieve();
            Assert.That(GetScanNumbers(results), Is.EqualTo(new List<int> { 1, 2, 3, 4, 5 }));

            //Open-ended interval
            parameters.scans = "41-";
            reader = new ProxiSpectrumReader(parameters);
            results = reader.Retrieve();
            Assert.That(GetScanNumbers(results), Is.EqualTo(new List<int> { 41, 42, 43, 44, 45, 46, 47, 48}));

            //Interval larger than available scans
            parameters.scans = "45-50";
            reader = new ProxiSpectrumReader(parameters);
            results = reader.Retrieve();
            Assert.That(GetScanNumbers(results), Is.EqualTo(new List<int> { 45, 46, 47, 48 }));

            //Sequence of scans to retrieve
            parameters.scans = "1,5,7";
            reader = new ProxiSpectrumReader(parameters);
            results = reader.Retrieve();
            Assert.That(GetScanNumbers(results), Is.EqualTo(new List<int> { 1, 5, 7 }));

            //Combination of intervals and individual scans
            parameters.scans = "-2,5,7-10,15,46-";
            reader = new ProxiSpectrumReader(parameters);
            results = reader.Retrieve();
            Assert.That(GetScanNumbers(results), Is.EqualTo(new List<int> { 1, 2, 5, 7, 8, 9, 10, 15, 46, 47, 48 }));
        }

        private List<int> GetScanNumbers(List<ProxiSpectrum> results)
        {
            List<int> scanNumbers = new List<int>();

            foreach (var result in results)
            {
                result.attributes.Where(a => a.Name == "scan number")
                    .ToList()
                    .ForEach(a => scanNumbers.Add(int.Parse(a.Value)));
            }

            return scanNumbers;
        }
    }
}
