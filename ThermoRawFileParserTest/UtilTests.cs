using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using ThermoRawFileParser;
using ThermoRawFileParser.Util;

namespace ThermoRawFileParserTest
{
    [TestFixture]
    public class UtilTests
    {
        [Test]
        public void TestRegex()
        {
            const string filterString = "ITMS + c NSI r d Full ms2 961.8803@cid35.00 [259.0000-1934.0000]";
            const string pattern = @"ms2 (.*?)@";

            Match result = Regex.Match(filterString, pattern);
            if (result.Success)
            {
                Assert.That(result.Groups[1].Value, Is.EqualTo("961.8803"));
            }
            else
            {
                Assert.Fail();
            }
        }

        [Test]
        public void TestNumberIterator()
        {
            NumberIterator iterator;
            iterator = new NumberIterator();
            Assert.That(new List<int>(iterator.IterateScans()),
                Is.EqualTo(new List<int>()));

            iterator = new NumberIterator("1, 2,3- 5, 7, 9 - 10", 1, 100);
            Assert.That(new List<int>(iterator.IterateScans()),
                Is.EqualTo(new List<int> { 1, 2, 3, 4, 5, 7, 9, 10 }));

            iterator = new NumberIterator(null, 1, 5);
            Assert.That(new List<int>(iterator.IterateScans()),
                Is.EqualTo(new List<int> { 1, 2, 3, 4, 5 }));

            iterator = new NumberIterator(" - ", 1, 5);
            Assert.That(new List<int>(iterator.IterateScans()),
                Is.EqualTo(new List<int> { 1, 2, 3, 4, 5 }));

            iterator = new NumberIterator("- 5, 9 - ", 1, 12);
            Assert.That(new List<int>(iterator.IterateScans()),
                Is.EqualTo(new List<int> { 1, 2, 3, 4, 5, 9, 10, 11, 12 }));

            Assert.Throws(typeof(Exception), () => new NumberIterator("1, 2, 2-5", 1, 10));
            Assert.Throws(typeof(Exception), () => new NumberIterator("3, -5", 1, 10));
            Assert.Throws(typeof(Exception), () => new NumberIterator("3 -,7", 1, 10));
            Assert.Throws(typeof(Exception), () => new NumberIterator("a,-,7", 1, 10));

        }

    }
}