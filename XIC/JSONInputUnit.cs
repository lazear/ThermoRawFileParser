﻿using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ThermoRawFileParser.XIC
{
    public class JSONInputUnit
    {
        [JsonProperty("mz_start", DefaultValueHandling = DefaultValueHandling.Populate)]
        [DefaultValue(-1)]
        public double MzStart { get; set; }
        [JsonProperty("mz_end", DefaultValueHandling = DefaultValueHandling.Populate)]
        [DefaultValue(-1)]
        public double MzEnd { get; set; }
        [JsonProperty("mz", DefaultValueHandling = DefaultValueHandling.Populate)]
        [DefaultValue(-1)]
        public double Mz { get; set; }
        [JsonProperty("sequence", DefaultValueHandling = DefaultValueHandling.Populate)]
        [DefaultValue("")]
        public string Sequence { get; set; }
        [JsonProperty("tolerance", DefaultValueHandling = DefaultValueHandling.Populate)]
        [DefaultValue(-1)] 
        public double Tolerance { get; set; }
        [JsonProperty("tolerance_unit", DefaultValueHandling = DefaultValueHandling.Populate)]
        [DefaultValue(-1)] 
        public string ToleranceUnit { get; set; }
        [JsonProperty("charge", DefaultValueHandling = DefaultValueHandling.Populate)]
        [DefaultValue("")] 
        public int Charge { get; set; }
        [JsonProperty("rt_start", DefaultValueHandling = DefaultValueHandling.Populate)]
        [DefaultValue(-1)] 
        public double RtStart { get; set; }
        [JsonProperty("rt_end", DefaultValueHandling = DefaultValueHandling.Populate)]
        [DefaultValue(-1)] 
        public double RtEnd { get; set; }
    }
}
