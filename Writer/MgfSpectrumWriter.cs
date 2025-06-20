using System;
using System.Reflection;
using System.Text;
using log4net;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.FilterEnums;
using ThermoFisher.CommonCore.Data.Interfaces;
using ThermoRawFileParser.Util;

namespace ThermoRawFileParser.Writer
{
    public class MgfSpectrumWriter : SpectrumWriter
    {
        private static readonly ILog Log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private const string PositivePolarity = "+";
        private const string NegativePolarity = "-";

        public MgfSpectrumWriter(ParseInput parseInput) : base(parseInput)
        {
            ParseInput.MsLevel.Remove(1); // MS1 spectra are not supposed to be in MGF
        }

        /// <inheritdoc />       
        public override void Write(IRawDataPlus rawFile, int firstScanNumber, int lastScanNumber)
        {
            if (!rawFile.HasMsData)
            {
                throw new RawFileParserException("No MS data in RAW file, no output will be produced");
            }

            ConfigureWriter(".mgf");
            using (Writer)
            {

                Log.Info("Processing " + (lastScanNumber - firstScanNumber + 1) + " scans");

                var lastScanProgress = 0;
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
                        var spectrumText = CreateMGFScan(rawFile, scanNumber);
                        if (!string.IsNullOrEmpty(spectrumText))
                        {
                            Writer.WriteLine(spectrumText);
                            Log.Debug("Spectrum written to file -- SCAN# " + scanNumber);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Scan #{scanNumber} cannot be processed because of the following exception: {ex.Message}");
                        Log.Debug($"{ex.StackTrace}\n{ex.InnerException}");
                        ParseInput.NewError();
                        continue;
                    }
                }

                if (ParseInput.LogFormat == LogFormat.DEFAULT)
                {
                    Console.WriteLine();
                }

            }
        }

        private string? CreateMGFScan(IRawDataPlus rawFile, int scanNumber)
        {
            int _precursorScanNumber = 0;
            StringBuilder mgfSpectrumText = new StringBuilder();
            string resultString = String.Empty;

            // Get the retention time
            var retentionTime = rawFile.RetentionTimeFromScanNumber(scanNumber);

            // Get the scan filter for this scan number
            var scanFilter = rawFile.GetFilterForScanNumber(scanNumber);

            // Get the scan event for this scan number
            var scanEvent = rawFile.GetScanEventForScanNumber(scanNumber);

            // Trailer extra data list
            ScanTrailer trailerData;

            try
            {
                trailerData = new ScanTrailer(rawFile.GetTrailerExtraInformation(scanNumber));
            }
            catch (Exception ex)
            {
                Log.WarnFormat("Cannot load trailer infromation for scan {0} due to following exception\n{1}", scanNumber, ex.Message);
                ParseInput.NewWarn();
                trailerData = new ScanTrailer();
            }

            // Get scan ms level
            var msLevel = (int)scanFilter.MSOrder;

            // Construct the precursor reference string for the title 
            var precursorReference = "";

            //Tracking precursor scan numbers for MSn scans
            if (msLevel == 1)
            {
                // Keep track of the MS1 scan number for precursor reference
                _precursorScanNumbers[""] = scanNumber;
            }
            else
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
                    _precursorScanNumber = trailerMasterScan.Value;
                }
                else //try getting it from the scan filter
                {
                    _precursorScanNumber = GetParentFromScanString(result.Groups[1].Value);
                }

                if (_precursorScanNumber > 0)
                {
                    precursorReference = ConstructSpectrumTitle((int)Device.MS, 1, _precursorScanNumber);
                }
                else
                {
                    Log.Error($"Cannot find precursor scan for scan# {scanNumber}");
                    _precursorTree[-2] = new PrecursorInfo(0, msLevel, FindLastReaction(scanEvent, msLevel), null);
                    ParseInput.NewError();
                }
            }

            if (ParseInput.MsLevel.Contains(msLevel))
            {
                var reaction = GetReaction(scanEvent, scanNumber);

                mgfSpectrumText.AppendLine("BEGIN IONS");
                if (!ParseInput.MgfPrecursor)
                {
                    mgfSpectrumText.AppendLine($"TITLE={ConstructSpectrumTitle((int)Device.MS, 1, scanNumber)}");
                }
                else
                {
                    mgfSpectrumText.AppendLine(
                        $"TITLE={ConstructSpectrumTitle((int)Device.MS, 1, scanNumber)} [PRECURSOR={precursorReference}]");
                }

                mgfSpectrumText.AppendLine($"SCANS={scanNumber}");
                mgfSpectrumText.AppendLine($"RTINSECONDS={(retentionTime * 60):f5}");

                int? charge = trailerData.AsPositiveInt("Charge State:");
                double? monoisotopicMz = trailerData.AsDouble("Monoisotopic M/Z:");
                double? isolationWidth =
                    trailerData.AsDouble("MS" + msLevel + " Isolation Width:");

                if (reaction != null)
                {
                    var selectedIonMz =
                        CalculateSelectedIonMz(reaction, monoisotopicMz, isolationWidth);

                    var selectedIonIntensity = (selectedIonMz > ZeroDelta && _precursorScanNumber > 0) ?
                        CalculatePrecursorPeakIntensity(rawFile, _precursorScanNumber, reaction.PrecursorMass, isolationWidth,
                            ParseInput.NoPeakPicking.Contains(msLevel - 1)) : 0;

                    mgfSpectrumText.AppendLine($"PEPMASS={selectedIonMz:f5} {selectedIonIntensity:f3}");
                }

                // Charge
                if (charge != null)
                {
                    // Scan polarity            
                    var polarity = PositivePolarity;
                    if (scanFilter.Polarity == PolarityType.Negative)
                    {
                        polarity = NegativePolarity;
                    }

                    mgfSpectrumText.AppendLine($"CHARGE={charge}{polarity}");
                }

                // Get scan mz data
                MZData mzData;

                try
                {
                    mzData = ReadMZData(rawFile, scanEvent, scanNumber,
                        !ParseInput.NoPeakPicking.Contains((int)scanFilter.MSOrder), //requestCentroidedData
                        ParseInput.ChargeData, //requestChargeData
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

                if (!(mzData.masses is null) && mzData.masses.Length > 0)
                {
                    //Sorting masses and intensities
                    if (!(mzData.charges is null) && mzData.charges.Length > 0)
                    {
                        for (var i = 0; i < mzData.masses.Length; i++)
                        {
                            mgfSpectrumText.AppendLine($"{mzData.masses[i]:f5} {mzData.intensities[i]:f3} {(int)mzData.charges[i]:d}");
                        }
                    }
                    else
                    {
                        for (var i = 0; i < mzData.masses.Length; i++)
                        {
                            mgfSpectrumText.AppendLine($"{mzData.masses[i]:f5} {mzData.intensities[i]:f3}");
                        }
                    }
                }
                else
                {
                    Log.WarnFormat("Spectrum {0} has no m/z data", scanNumber);
                    ParseInput.NewWarn();
                }

                mgfSpectrumText.Append("END IONS");
                resultString = mgfSpectrumText.ToString();
            }

            return resultString;
        }
    }
}