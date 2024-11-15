using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using log4net;
using Parquet.Serialization;
using ThermoFisher.CommonCore.Data;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.FilterEnums;
using ThermoFisher.CommonCore.Data.Interfaces;

namespace ThermoRawFileParser.Writer
{

    struct MzParquet
    {
        public uint scan;
        public uint level;
        public float rt;
        public float mz;
        public float intensity;
        public float? ion_mobility;
        public float? isolation_lower;
        public float? isolation_upper;
        public uint? precursor_scan;
        public float? precursor_mz;
        public uint? precursor_charge;
    }

    public class ParquetSpectrumWriter : SpectrumWriter
    {
        private static readonly ILog Log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public ParquetSpectrumWriter(ParseInput parseInput) : base(parseInput)
        {
        }

        public override void Write(IRawDataPlus raw, int firstScanNumber, int lastScanNumber)
        {
            if (!raw.HasMsData)
            {
                throw new RawFileParserException("No MS data in RAW file, no output will be produced");
            }

            var enumerator = raw.GetFilteredScanEnumerator(" ");

            // NB: replace with more robust strategy?
            var output = ParseInput.OutputDirectory + "//" + Path.GetFileNameWithoutExtension(ParseInput.RawFilePath) + ".mzparquet";

            ParquetSerializerOptions opts = new ParquetSerializerOptions();
            opts.CompressionLevel = System.IO.Compression.CompressionLevel.Fastest;
            opts.CompressionMethod = Parquet.CompressionMethod.Zstd;

            var data = new List<MzParquet>();

            // map last (msOrder - 1) -> scan number (e.g. mapping precursors)
            // note, this assumes time dependence of MS1 -> MS2 -> MSN
            var last_scans = new Dictionary<int, uint>();


            foreach (var scanNumber in enumerator) 
            {
                var scanFilter = raw.GetFilterForScanNumber(scanNumber);

                CentroidStream centroidStream = new CentroidStream();

                // Pull out m/z and intensity values
                // NB: is this the best way to do this?
                if (scanFilter.MassAnalyzer == MassAnalyzerType.MassAnalyzerFTMS)
                {
                    centroidStream = raw.GetCentroidStream(scanNumber, false);
                }
                else if (scanFilter.MassAnalyzer == MassAnalyzerType.MassAnalyzerITMS)
                {
                    var scanData = raw.GetSimplifiedScan(scanNumber);
                    centroidStream.Masses = scanData.Masses;
                    centroidStream.Intensities = scanData.Intensities;
                }
                else
                {
                    var scanData = raw.GetSimplifiedCentroids(scanNumber);
                    centroidStream.Masses = scanData.Masses;
                    centroidStream.Intensities = scanData.Intensities;
                }


                var msOrder = raw.GetScanEventForScanNumber(scanNumber).MSOrder;

                last_scans[(int)msOrder] = (uint)scanNumber;

                double rt = raw.RetentionTimeFromScanNumber(scanNumber);
                float? isolation_lower = null;
                float? isolation_upper = null;
                uint? precursor_scan = null;
                float? precursor_mz = null;
                uint? precursor_charge = null;

                if ((int)msOrder > 1)
                {
                    var rx = scanFilter.GetReaction(0);

                    // this assumes symmetrical quad window
                    isolation_lower = (float)(rx.PrecursorMass - rx.IsolationWidth / 2);
                    isolation_upper = (float)(rx.PrecursorMass + rx.IsolationWidth / 2);
                    precursor_mz = (float)rx.PrecursorMass;

                    // Try to retrieve last scan that occurred at the previous msOrder
                    uint t;
                    if (last_scans.TryGetValue((int)msOrder - 1, out t))
                    {
                        precursor_scan = t;
                    }
                }

                var trailer = raw.GetTrailerExtraInformation(scanNumber);
                for (var i = 0l; i < trailer.Length; i++)
                {

                    if (trailer.Labels[i].StartsWith("Monoisotopic M/Z"))
                    {
                        var val = float.Parse(trailer.Values[i]);
                        if (val > 0)
                        {
                            precursor_mz = val;
                        }
                    }

                    // Overwrite precursor_scan with value from trailer, if it exists
                    if (trailer.Labels[i].StartsWith("Master Scan"))
                    {
                        var val = Int64.Parse(trailer.Values[i]);
                        if (val > 0)
                        {
                            precursor_scan = (uint)val;
                        }
                    }

                    if (trailer.Labels[i].StartsWith("Charge"))
                    {
                        var val = uint.Parse(trailer.Values[i]);
                        if (val > 0)
                        {
                            precursor_charge = val;
                        }
                    }
                }

                // Add a row to parquet file for every m/z value in this scan
                for (int i = 0; i < centroidStream.Masses.Length; i++)
                {
                    MzParquet m;
                    m.rt = (float)rt;
                    m.scan = (uint)scanNumber;
                    m.level = ((uint)msOrder);
                    m.intensity = (float)centroidStream.Intensities[i];
                    m.mz = (float)centroidStream.Masses[i];
                    m.isolation_lower = isolation_lower;
                    m.isolation_upper = isolation_upper;
                    m.precursor_scan = precursor_scan;
                    m.precursor_mz = precursor_mz;
                    m.precursor_charge = precursor_charge;
                    m.ion_mobility = null;
                    data.Add(m);
                }

                // If we have enough ions to write a row group, do so
                // - some row groups might have more than this number of ions
                //   but this ensures that all ions from a single scan are always
                //   present in the same row group (critical property of mzparquet)
                if (data.Count >= 1_048_576)
                {
                    var task = ParquetSerializer.SerializeAsync(data, output, opts);
                    task.Wait();
                    opts.Append = true;
                    data.Clear();
                    Log.Info("writing row group");
                }

            }

            // serialize any remaining ions into the final row group
            if (data.Count > 0)
            {
                var task = ParquetSerializer.SerializeAsync(data, output, opts);
                task.Wait();
                Log.Info("writing row group");
            }
        }
    }

}