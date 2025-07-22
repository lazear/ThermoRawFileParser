using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Newtonsoft.Json;

namespace ThermoRawFileParser.Query
{
    public class QueryExecutor
    {
        public static void Run(QueryParameters parameters)
        {
            
            var reader = new ProxiSpectrumReader(parameters);
            var results = reader.Retrieve();

            if (parameters.stdout)
            {
                StdOutputQueryData(results);
            }
            else
            {
                string outputFileName;

                // if outputFile has been defined, put output there.
                if (parameters.outputFile != null)
                {
                    outputFileName = Path.GetFullPath(parameters.outputFile);
                }
                // otherwise put output files into the same directory as the raw file input
                else
                {
                    outputFileName = Path.GetFullPath(parameters.userFilePath);
                }

                var directory = Path.GetDirectoryName(outputFileName);

                outputFileName = Path.Combine(directory ?? throw new NoNullAllowedException(),
                    Path.GetFileNameWithoutExtension(outputFileName) + ".json");

                OutputQueryData(results, outputFileName);
            }
        }

        private static void OutputQueryData(List<ProxiSpectrum> outputData, string outputFileName)
        {
            var outputString = JsonConvert.SerializeObject(outputData, Formatting.Indented);
            File.WriteAllText(outputFileName, outputString);
        }


        private static void StdOutputQueryData(List<ProxiSpectrum> outputData)
        {
            var outputString = JsonConvert.SerializeObject(outputData, Formatting.Indented);
            Console.Write(outputString);
        }
    }
}