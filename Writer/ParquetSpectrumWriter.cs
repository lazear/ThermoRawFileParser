using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using log4net;
using Parquet.Serialization;
using ThermoFisher.CommonCore.Data;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.FilterEnums;
using ThermoFisher.CommonCore.Data.Interfaces;
using ThermoRawFileParser.Writer.MzML;

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
        public int? precursor_scan;
        public float? precursor_mz;
        public uint? precursor_charge;
    }

    struct PrecursorData
    {
        public float? mz;
        public float? isolation_lower;
        public float? isolation_upper;
    }

    public class ParquetSpectrumWriter : SpectrumWriter
    {
        private static readonly ILog Log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly Regex _filterStringIsolationMzPattern = new Regex(@"ms\d+ (.+?) \[");

        // Precursor scan number (value) and isolation m/z (key) for reference in the precursor element of an MSn spectrum
        private readonly Dictionary<string, int> _precursorScanNumbers = new Dictionary<string, int>();

        //Precursor information for scans
        private Dictionary<int, PrecursorInfo> _precursorTree = new Dictionary<int, PrecursorInfo>();

        public ParquetSpectrumWriter(ParseInput parseInput) : base(parseInput)
        {
            _precursorScanNumbers[""] = -1;
            _precursorTree[-1] = new PrecursorInfo();
        }

        public override void Write(IRawDataPlus raw, int firstScanNumber, int lastScanNumber)
        {
            if (!raw.HasMsData)
            {
                throw new RawFileParserException("No MS data in RAW file, no output will be produced");
            }

            ConfigureWriter(".mzparquet");
            
            ParquetSerializerOptions opts = new ParquetSerializerOptions();
            opts.CompressionLevel = System.IO.Compression.CompressionLevel.Fastest;
            opts.CompressionMethod = Parquet.CompressionMethod.Zstd;

            var data = new List<MzParquet>();

            var lastScanProgress = 0;

            Log.Info(String.Format("Processing {0} MS scans", +(1 + lastScanNumber - firstScanNumber)));

            for (var scanNumber = firstScanNumber; scanNumber <= lastScanNumber; scanNumber++)
            {
                if (ParseInput.LogFormat == LogFormat.DEFAULT)
                {
                    var scanProgress = (int)((double)scanNumber / (lastScanNumber - firstScanNumber + 1) * 100);
                    if (scanProgress % ProgressPercentageStep == 0)
                    {
                        if (scanProgress != lastScanProgress)
                        {
                            Console.Write("" + scanProgress + "% ");
                            lastScanProgress = scanProgress;
                        }
                    }
                }

                try
                {
                    int level = (int)raw.GetScanEventForScanNumber(scanNumber).MSOrder; //applying MS level filter
                    if (ParseInput.MsLevel.Contains(level))
                        AddScan(raw, scanNumber, data);
                }
                catch (Exception ex)
                {
                    Log.Error($"Scan #{scanNumber} cannot be processed because of the following exception: {ex.Message}\n{ex.StackTrace}");
                    ParseInput.NewError();
                }

                // If we have enough ions to write a row group, do so
                // - some row groups might have more than this number of ions
                //   but this ensures that all ions from a single scan are always
                //   present in the same row group (critical property of mzparquet)
                if (data.Count >= 1_048_576)
                {
                    var task = ParquetSerializer.SerializeAsync(data, Writer.BaseStream, opts);
                    task.Wait();
                    opts.Append = true;
                    data.Clear();
                    Log.Debug("Writing next row group");
                }
            }

            // serialize any remaining ions into the final row group
            if (data.Count > 0)
            {
                var task = ParquetSerializer.SerializeAsync(data, Writer.BaseStream, opts);
                task.Wait();
                Log.Debug("Writing final row group");
            }
        }

        private void AddScan(IRawDataPlus raw, int scanNumber, List<MzParquet> data)
        {
            var scanFilter = raw.GetFilterForScanNumber(scanNumber);

            // Get the scan event for this scan number
            var scanEvent = raw.GetScanEventForScanNumber(scanNumber);

            // Get scan ms level
            var msLevel = (int)scanFilter.MSOrder;

            // Get Scan
            var scan = Scan.FromFile(raw, scanNumber);
            ScanTrailer trailerData;

            try
            {
                trailerData = new ScanTrailer(raw.GetTrailerExtraInformation(scanNumber));
            }
            catch (Exception ex)
            {
                Log.WarnFormat("Cannot load trailer infromation for scan {0} due to following exception\n{1}", scanNumber, ex.Message);
                ParseInput.NewWarn();
                trailerData = new ScanTrailer();
            }

            int? trailer_charge = trailerData.AsPositiveInt("Charge State:");
            double? trailer_mz = trailerData.AsDouble("Monoisotopic M/Z:");
            double? trailer_isolationWidth = trailerData.AsDouble("MS" + msLevel + " Isolation Width:");
            double? FAIMSCV = null;
            if (trailerData.AsBool("FAIMS Voltage On:").GetValueOrDefault(false))
                FAIMSCV = trailerData.AsDouble("FAIMS CV:");

            double rt = raw.RetentionTimeFromScanNumber(scanNumber);
            int precursor_scan = 0;
            PrecursorData precursor_data = new PrecursorData
            {
                isolation_lower = null,
                isolation_upper = null,
                mz = null

            };

            if (msLevel == 1)
            {
                // Keep track of scan number for precursor reference
                _precursorScanNumbers[""] = scanNumber;
                _precursorTree[scanNumber] = new PrecursorInfo();
            }
            else if (msLevel > 1)
            {
                // Keep track of scan number and isolation m/z for precursor reference                   
                var result = _filterStringIsolationMzPattern.Match(scanEvent.ToString());
                if (result.Success)
                {
                    if (_precursorScanNumbers.ContainsKey(result.Groups[1].Value))
                    {
                        _precursorScanNumbers.Remove(result.Groups[1].Value);
                    }

                    _precursorScanNumbers.Add(result.Groups[1].Value, scanNumber);
                }

                //update precursor scan if it is provided in trailer data
                var trailerMasterScan = trailerData.AsPositiveInt("Master Scan Number:");
                if (trailerMasterScan.HasValue)
                {
                    precursor_scan = trailerMasterScan.Value;
                }
                else //try getting it from the scan filter
                {
                    precursor_scan = GetParentFromScanString(result.Groups[1].Value);
                }

                //finding precursor scan failed
                if (precursor_scan == -2)
                {
                    Log.Warn($"Cannot find precursor scan for scan# {scanNumber}");
                    _precursorTree[-2] = new PrecursorInfo(0, msLevel, FindLastReaction(scanEvent, msLevel), null);
                }

                //Parsing the last reaction
                try
                {
                    try //since there is no direct way to get the number of reactions available, it is necessary to try and fail
                    {
                        scanEvent.GetReaction(_precursorTree[precursor_scan].ReactionCount);
                    }
                    catch (ArgumentOutOfRangeException ex)
                    {
                        Log.Debug($"Using Tribrid decision tree fix for scan# {scanNumber}");
                        //Is it a decision tree scheduled scan on tribrid?
                        if (msLevel == _precursorTree[precursor_scan].MSLevel)
                        {
                            precursor_scan = GetParentFromScanString(result.Groups[1].Value);
                        }
                        else
                        {
                            throw new RawFileParserException(
                                $"Tribrid decision tree fix failed - cannot get reaction# {_precursorTree[precursor_scan].ReactionCount} from {scanEvent.ToString()}",
                                ex);
                        }
                    }

                    // Get Precursor m/z and isolation window borders
                    precursor_data = GetPrecursorData(precursor_scan, scanEvent, trailer_mz, trailer_isolationWidth, out var reactionCount);

                    //save precursor information for later reference
                    _precursorTree[scanNumber] = new PrecursorInfo(precursor_scan, msLevel, reactionCount, null);
                }
                catch (Exception e)
                {
                    var extra = (e.InnerException is null) ? "" : $"\n{e.InnerException.StackTrace}";

                    Log.Warn($"Could not get precursor data for scan# {scanNumber} - precursor information for this and dependent scans will be empty\nException details:{e.Message}\n{e.StackTrace}\n{extra}");
                    ParseInput.NewWarn();

                    _precursorTree[scanNumber] = new PrecursorInfo(precursor_scan, 1, 0, null);
                }

            }

            double[] masses;
            double[] intensities;

            if (!ParseInput.NoPeakPicking.Contains(msLevel))
            {
                // Check if the scan has a centroid stream
                if (scan.HasCentroidStream)
                {
                    masses = scan.CentroidScan.Masses;
                    intensities = scan.CentroidScan.Intensities;
                }
                else // otherwise take the segmented (low res) scan
                {
                    // If the spectrum is profile perform centroiding
                    var segmentedScan = scanEvent.ScanData == ScanDataType.Profile
                        ? Scan.ToCentroid(scan).SegmentedScan
                        : scan.SegmentedScan;

                    masses = segmentedScan.Positions;
                    intensities = segmentedScan.Intensities;
                }
            }
            else // use the segmented data as is
            {
                masses = scan.SegmentedScan.Positions;
                intensities = scan.SegmentedScan.Intensities;
            }

            // Add a row to parquet file for every m/z value in this scan
            for (int i = 0; i < masses.Length; i++)
            {
                MzParquet m;
                m.rt = (float)rt;
                m.scan = (uint)scanNumber;
                m.level = (uint)msLevel;
                m.intensity = (float)intensities[i];
                m.mz = (float)masses[i];
                m.isolation_lower = precursor_data.isolation_lower;
                m.isolation_upper = precursor_data.isolation_upper;
                m.precursor_scan = precursor_scan;
                m.precursor_mz = precursor_data.mz;
                m.precursor_charge = (uint?)trailer_charge;
                m.ion_mobility = (float?)FAIMSCV;
                data.Add(m);
            }
        }

        private int GetParentFromScanString(string scanString)
        {
            var parts = Regex.Split(scanString, " ");

            //find the position of the first (from the end) precursor with a different mass 
            //to account for possible supplementary activations written in the filter
            var lastIonMass = parts.Last().Split('@').First();
            int last = parts.Length;
            while (last > 0 &&
                   parts[last - 1].Split('@').First() == lastIonMass)
            {
                last--;
            }

            string parentFilter = String.Join(" ", parts.Take(last));
            if (_precursorScanNumbers.ContainsKey(parentFilter))
            {
                return _precursorScanNumbers[parentFilter];
            }

            return -2; //unsuccessful parsing
        }

        
        private int FindLastReaction(IScanEvent scanEvent, int msLevel)
        {
            int lastReactionIndex = msLevel - 2;

            //iteratively trying find the last available index for reaction
            while (true)
            {
                try
                {
                    scanEvent.GetReaction(lastReactionIndex + 1);
                }
                catch (ArgumentOutOfRangeException)
                {
                    //stop trying
                    break;
                }

                lastReactionIndex++;
            }

            //supplemental activation flag is on -> one of the levels (not necissirily the last one) used supplemental activation
            //check last two activations
            if (scanEvent.SupplementalActivation == TriState.On)
            {
                var lastActivation = scanEvent.GetReaction(lastReactionIndex).ActivationType;
                var beforeLastActivation = scanEvent.GetReaction(lastReactionIndex - 1).ActivationType;

                if ((beforeLastActivation == ActivationType.ElectronTransferDissociation || beforeLastActivation == ActivationType.ElectronCaptureDissociation) &&
                    (lastActivation == ActivationType.CollisionInducedDissociation || lastActivation == ActivationType.HigherEnergyCollisionalDissociation))
                    return lastReactionIndex - 1; //ETD or ECD followed by HCD or CID -> supplemental activation in the last level (move the last reaction one step back)
                else
                    return lastReactionIndex;
            }
            else //just use the last one
            {
                return lastReactionIndex;
            }
        }

        private PrecursorData GetPrecursorData(int precursorScanNumber, IScanEventBase scanEvent,
            double? monoisotopicMz, double? isolationWidth, out int reactionCount)
        {
            double? isolation_lower = null;
            double? isolation_upper = null;

            // Get precursors from earlier levels
            var prevPrecursors = _precursorTree[precursorScanNumber];
            reactionCount = prevPrecursors.ReactionCount;

            var reaction = scanEvent.GetReaction(reactionCount);

            //if isolation width was not found in the trailer, try to get one from the reaction
            if (isolationWidth == null) isolationWidth = reaction.IsolationWidth;
            if (isolationWidth < 0) isolationWidth = null;

            // Selected ion MZ
            var selectedIonMz = CalculateSelectedIonMz(reaction, monoisotopicMz, isolationWidth);

            if (isolationWidth != null)
            {
                var offset = isolationWidth.Value / 2 + reaction.IsolationWidthOffset;
                isolation_lower = reaction.PrecursorMass - isolationWidth.Value + offset;
                isolation_upper = reaction.PrecursorMass + offset;
            }

            // Activation only to keep track of the reactions
            //increase reaction count
            reactionCount++;

            //Sometimes the property of supplemental activation is not set (Tune v4 on Tribrid),
            //or is On if *at least* one of the levels had SA (i.e. not necissirily the last one), thus we need to try (and posibly fail)
            try
            {
                reaction = scanEvent.GetReaction(reactionCount);

                if (reaction != null)
                {
                    //increase reaction count after successful parsing
                    reactionCount++;
                }
            }
            catch (IndexOutOfRangeException)
            {
                // If we failed do nothing
            }
            
            return new PrecursorData
            {
                mz = (float?)selectedIonMz,
                isolation_lower = (float?)isolation_lower,
                isolation_upper = (float?)isolation_upper
            };

        }
    }

}