using System;
using System.Collections.Generic;
using System.Reflection;
using log4net;
using Parquet.Serialization;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.FilterEnums;
using ThermoFisher.CommonCore.Data.Interfaces;
using ThermoRawFileParser.Util;

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

        private const int ParquetSliceSize = 1_048_576;

        public ParquetSpectrumWriter(ParseInput parseInput) : base(parseInput)
        {
            //nothing to do here
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
                    if (level <= ParseInput.MaxLevel) // Primary MS level filter
                    {
                        var scanData = ReadScan(raw, scanNumber);
                        if (scanData != null && ParseInput.MsLevel.Contains(level)) // Final MS level filter
                            data.AddRange(scanData);
                    }
                    
                }
                catch (Exception ex)
                {
                    Log.Error($"Scan #{scanNumber} cannot be processed because of the following exception: {ex.Message}");
                    Log.Debug($"{ex.StackTrace}\n{ex.InnerException}");
                    ParseInput.NewError();
                }

                // If we have enough ions to write a row group, do so
                // - some row groups might have more than this number of ions
                //   but this ensures that all ions from a single scan are always
                //   present in the same row group (critical property of mzparquet)
                if (data.Count >= ParquetSliceSize)
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

            if (ParseInput.LogFormat == LogFormat.DEFAULT) //Add new line after progress bar
            {
                Console.WriteLine();
            }

            // Release the OS file handle
            Writer.Flush();
            Writer.Close();
        }

        private List<MzParquet> ReadScan(IRawDataPlus raw, int scanNumber)
        {
            var scanFilter = raw.GetFilterForScanNumber(scanNumber);

            // Get the scan event for this scan number
            var scanEvent = raw.GetScanEventForScanNumber(scanNumber);

            // Get scan ms level
            var msLevel = (int)scanFilter.MSOrder;

            // Get Scan trailer
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

            MZData mzData;

            // Get each mz data for scan
            try
            {
                mzData = ReadMZData(raw, scanEvent, scanNumber,
                    !ParseInput.NoPeakPicking.Contains((int)scanFilter.MSOrder), //requestCentroidedData
                    false, //requestChargeData
                    false); //requestNoiseData
            }
            catch (Exception ex)
            {
                Log.ErrorFormat("Failed reading mz data for scan #{0} due to following exception: {1}\nMZ data will be empty", scanNumber, ex.Message);
                Log.DebugFormat("{0}\n{1}", ex.StackTrace, ex.InnerException);
                ParseInput.NewError();

                mzData = new MZData
                {
                    basePeakMass = null,
                    basePeakIntensity = null,
                    masses = Array.Empty<double>(),
                    intensities = Array.Empty<double>(),
                    charges = Array.Empty<double>(),
                    baselineData = Array.Empty<double>(),
                    noiseData = Array.Empty<double>(),
                    massData = Array.Empty<double>(),
                    isCentroided = false
                };
            }

            if (mzData.masses.Length == 0 || mzData.intensities.Length == 0)
            {
                Log.WarnFormat("Spectrum {0} has no m/z data", scanNumber);
            }

            List<MzParquet> scanData = new List<MzParquet>(mzData.masses.Length);
            // Add a row to parquet file for every m/z value in this scan
            for (int i = 0; i < mzData.masses.Length; i++)
            {
                MzParquet m;
                m.rt = (float)rt;
                m.scan = (uint)scanNumber;
                m.level = (uint)msLevel;
                m.intensity = (float)mzData.intensities[i];
                m.mz = (float)mzData.masses[i];
                m.isolation_lower = precursor_data.isolation_lower;
                m.isolation_upper = precursor_data.isolation_upper;
                m.precursor_scan = precursor_scan;
                m.precursor_mz = precursor_data.mz;
                m.precursor_charge = (uint?)trailer_charge;
                m.ion_mobility = (float?)FAIMSCV;
                scanData.Add(m);
            }

            return scanData;
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