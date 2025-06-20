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
}
