using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Reflection;
using log4net;
using Parquet;
using Parquet.Data;
using ThermoFisher.CommonCore.Data;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.FilterEnums;
using ThermoFisher.CommonCore.Data.Interfaces;
using ThermoRawFileParser.Writer.MzML;
using System.Diagnostics;

namespace ThermoRawFileParser.Writer
{

    struct PScan
    {
        public int scan;
        public int level;
        public float rt;
        public float? precursor_mz;
        public int? precursor_charge;
        public float mz;
        public float intensity;
        public int? charge;
        public float? noise;
    }

    public class ParquetSpectrumWriter : SpectrumWriter
    {
        private static readonly ILog Log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);


        public ParquetSpectrumWriter(ParseInput parseInput) : base(parseInput)
        {
        }

        public override void Write(IRawDataPlus rawFile, int firstScanNumber, int lastScanNumber)
        {

            var scans = Extract(rawFile);
            WriteScans(scans, ParseInput.OutputDirectory, rawFile.FileName);
        }

        private static List<PScan> Extract(IRawDataPlus raw)
        {
            var scans = new List<PScan>();
            var enumerator = raw.GetFilteredScanEnumerator(" ");

            foreach (var scanNumber in enumerator)
            {

                //trailer information is extracted via index
                //var trailers = raw.GetTrailerExtraValues(scanNumber);
                var trailerLabels = raw.GetTrailerExtraInformation(scanNumber);
                object chargeState = 0;
                for (int i = 0; i < trailerLabels.Labels.Length; i++)
                {
                    if (trailerLabels.Labels[i] == "Charge State:")
                    {
                        chargeState = raw.GetTrailerExtraValue(scanNumber, i);
                        break;
                    }
                }

                var scanFilter = raw.GetFilterForScanNumber(scanNumber);
                int msLevel = (int)scanFilter.MSOrder;
                var scanStats = raw.GetScanStatsForScanNumber(scanNumber);

                CentroidStream centroidStream = new CentroidStream();

                //check for FT mass analyzer data
                if (scanFilter.MassAnalyzer == MassAnalyzerType.MassAnalyzerFTMS)
                {
                    centroidStream = raw.GetCentroidStream(scanNumber, false);
                }

                //check for IT mass analyzer data
                if (scanFilter.MassAnalyzer == MassAnalyzerType.MassAnalyzerITMS)
                {
                    var scanData = raw.GetSimplifiedScan(scanNumber);
                    centroidStream.Masses = scanData.Masses;
                    centroidStream.Intensities = scanData.Intensities;
                }

                if (msLevel == 1)
                {
                    foreach (var scan in Melt(scanStats, centroidStream, msLevel))
                    {
                        scans.Add(scan);
                    }
                }
                else if (msLevel > 1)
                {
                    var precursorMz = (float) raw.GetScanEventForScanNumber(scanNumber).GetReaction(0).PrecursorMass;
                    foreach (var scan in Melt(scanStats, centroidStream, msLevel, precursorMz,
                        Convert.ToInt32(chargeState)))
                    {
                        scans.Add(scan);
                    }
                }
            }
            return scans;
        }

        // Convert scan data to long format
        private static List<PScan> Melt(ScanStatistics scanStats, CentroidStream centroidStream,
            int level, float? precursorMz = null, int? precursorCharge = null)
        {
            var scans = new List<PScan>();
            for (int i = 0; i < centroidStream.Masses.Length; i++)
            {
                PScan s;
                s.scan = scanStats.ScanNumber;
                s.level = level;
                s.rt = (float) scanStats.StartTime;
                s.precursor_mz = precursorMz;
                s.precursor_charge =  precursorCharge;
                s.intensity = (float) centroidStream.Intensities[i];
                s.mz = (float)centroidStream.Masses[i];
                s.charge = null;
                s.noise = null;

                if (centroidStream.Charges.Length > i)
                {
                    s.charge = Convert.ToInt32(centroidStream.Charges[i]);
                }
                if (centroidStream.Noises.Length > i)
                {
                    s.noise = (float)centroidStream.Noises[i];
                }
                scans.Add(s);
            }
            return scans;
        }

        private static void WriteScans(IEnumerable<PScan> scans, string outputDirectory, string sourceRawFileName)
        {
            var output = outputDirectory + "//" + Path.GetFileNameWithoutExtension(sourceRawFileName);

            var ds = new DataSet(
                new DataField<string>("file"),
                new DataField<int>("scan"),
                new DataField<int>("level"),
                new DataField<float>("rt"),
                new DataField<float?>("precursor_mz"),
                new DataField<int?>("precursor_charge"),
                new DataField<float>("mz"),
                new DataField<float>("intensity"),
                new DataField<int?>("charge"),
                new DataField<float?>("noise")
             );

            foreach ( var s in scans )
            {
                ds.Add(
                    sourceRawFileName,
                    s.scan,
                    s.level,
                    s.rt,
                    s.precursor_mz,
                    s.precursor_charge,
                    s.mz,
                    s.intensity,
                    s.charge,
                    s.noise
                    );
            }


            using (Stream fileStream = File.Open(output + ".parquet", FileMode.Create))
            {
                using (var writer = new ParquetWriter(fileStream))
                {
                    writer.Write(ds, CompressionMethod.Gzip);
                }
            }
        }
    }
}
