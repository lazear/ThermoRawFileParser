using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ThermoFisher.CommonCore.Data.FilterEnums;
using ThermoFisher.CommonCore.Data.Interfaces;

namespace ThermoRawFileParser.Writer
{
    static public class WriterUtil
    {
        public static Dictionary<MSOrderType, int> CountScanOrder(IRawDataPlus rawFile, int firstScan, int lastScan)
        {
            var scanOrderCounts = new Dictionary<MSOrderType, int>();
            foreach (MSOrderType item in Enum.GetValuesAsUnderlyingType(typeof(MSOrderType)))
            {
                scanOrderCounts[item] = 0;
            }

            for (int scan=firstScan; scan <=lastScan; scan++)
            {
                var filter = rawFile.GetFilterForScanNumber(scan);
                scanOrderCounts[filter.MSOrder] += 1;
            }

            scanOrderCounts[MSOrderType.Any] = scanOrderCounts.Values.Sum();

            return scanOrderCounts;
        }

        public static Dictionary<MSOrderType, int> CountScanOrder(IRawDataPlus rawFile)
        {
            return CountScanOrder(rawFile, rawFile.RunHeaderEx.FirstSpectrum, rawFile.RunHeaderEx.LastSpectrum);
        }
    }
}
