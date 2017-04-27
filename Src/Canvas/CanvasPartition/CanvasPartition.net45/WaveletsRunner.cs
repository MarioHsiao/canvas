using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanvasCommon;
using Illumina.Common;

namespace CanvasPartition
{
    class WaveletsRunner
    {
        private WaveletsRunnerParams _parameters;
        public static readonly double DefaultMadFactor = 2.0; // default MAD factor for Wavelets

        public class WaveletsRunnerParams
        {
            public bool IsGermline { get; }
            public double EvennessScoreThreshold { get; }
            public string CommonCNVs { get; }
            public double ThresholdLower { get; }
            public double ThresholdUpper { get; }
            public double MadFactor { get; }
            public int MinSize { get; }
            public int Verbose { get; }
            public bool IsSmallPedegree { get; }

            public WaveletsRunnerParams(bool isGermline, string commonCNVs = null, double evennessScoreThreshold = 94.50, 
                double thresholdLower = 5, double thresholdLowerMaf = 0.05, double thresholdUpper = 80, double madFactor = 2, int minSize = 10, 
                int verbose = 1, bool isSmallPedegree = false)
            {
                IsGermline = isGermline;
                EvennessScoreThreshold = evennessScoreThreshold;
                CommonCNVs = commonCNVs;           
                ThresholdLower = thresholdLowerMaf;
                ThresholdUpper = thresholdUpper;
                MadFactor = madFactor;
                MinSize = minSize;
                Verbose = verbose;
                IsSmallPedegree = isSmallPedegree;
            }
        }

        public WaveletsRunner(WaveletsRunnerParams waveletsRunnerParams)
        {
            _parameters = waveletsRunnerParams;
        }

        /// <summary>
        /// Wavelets: unbalanced HAAR wavelets segmentation 
        /// </summary>
        public Dictionary<string, SegmentationInput.Segment[]> Run(SegmentationInput segmentationInput)
        {
            double evennessScore = segmentationInput.GetEvennessScore();
            bool useVaf = evennessScore < _parameters.EvennessScoreThreshold;
            if (!segmentationInput.CoverageMetricsFile.IsNullOrEmpty())
                CanvasIO.WriteCoverageMetricToTextFile(segmentationInput.CoverageMetricsFile, evennessScore, CanvasIO.CoverageMetric.evenness);
            Dictionary<string, List<int>> breakpoints;
            Dictionary<string, List<int>> adjustedBreakpoints;

            if (!useVaf)
            {
                breakpoints = LaunchWavelets(segmentationInput.CoverageByChr, segmentationInput.StartByChr,
                    segmentationInput.EndByChr);
                adjustedBreakpoints = AdjustBreakpoints(segmentationInput.CoverageByChr, segmentationInput, breakpoints, vafContainingBinsByChr:null);
            }
            else
            {
                var vafByChr = new Dictionary<string, double[]>();
                var vafContainingBinsByChr = new Dictionary<string, int[]>();

                foreach (string chr in segmentationInput.VafByChr.Keys)
                {
                    vafByChr[chr] = segmentationInput.VafByChr[chr].Select(vafContainingBins => vafContainingBins.Vaf).ToArray();
                    vafContainingBinsByChr[chr] = segmentationInput.VafByChr[chr].Select(coverageToVafMapper => coverageToVafMapper.Index).ToArray();
                }
                breakpoints = LaunchWavelets(vafByChr, segmentationInput.StartByChr, segmentationInput.EndByChr);
                adjustedBreakpoints = AdjustBreakpoints(vafByChr, segmentationInput, breakpoints, vafContainingBinsByChr);
            }

            var segments = new Dictionary<string, SegmentationInput.Segment[]>();
            foreach (string chr in segmentationInput.VafByChr.Keys)
            {
                segments[chr] = SegmentationInput.DeriveSegments(adjustedBreakpoints[chr], segmentationInput.CoverageByChr[chr].Length,
                    segmentationInput.StartByChr[chr], segmentationInput.EndByChr[chr]);
            }
            return segments;
        }

        public new Dictionary<string, List<int>> LaunchWavelets(Dictionary<string, double[]> coverageByChr, Dictionary<string, uint[]> startByChr,
            Dictionary<string, uint[]> endByChr)
        {
            var inaByChr = new Dictionary<string, int[]>();
            var finiteScoresByChr = new Dictionary<string, double[]>();

            var tasks = coverageByChr.Select(scoreByChrKVP => new ThreadStart(() =>
            {
                string chr = scoreByChrKVP.Key;
                Helper.GetFiniteIndices(scoreByChrKVP.Value, out int[] ina); // not NaN, -Inf, Inf

                double[] scores;
                if (ina.Length == scoreByChrKVP.Value.Length)
                    scores = scoreByChrKVP.Value;
                else
                    Helper.ExtractValues<double>(scoreByChrKVP.Value, ina, out scores);

                lock (finiteScoresByChr)
                {
                    finiteScoresByChr[chr] = scores;
                    inaByChr[chr] = ina;
                }
            })).ToList();


            Parallel.ForEach(tasks, task => task.Invoke());
            // Quick sanity-check: If we don't have any segments, then return a dummy result.
            int n = finiteScoresByChr.Values.Sum(list => list.Length);
            if (n == 0)
                return new Dictionary<string, List<int>>();

            var breakpointsByChr = new Dictionary<string, List<int>>();
            tasks = coverageByChr.Keys.Select(chr => new ThreadStart(() =>
            {
                var breakpoints = new List<int>();
                // to cover cases of no SNVs present (i.e. chrY) => chromosome becomes one segment
                int segmentLengthByChr = Math.Max(coverageByChr[chr].Length, 1);
                if (segmentLengthByChr > _parameters.MinSize)
                {
                    WaveletSegmentation.HaarWavelets(coverageByChr[chr], _parameters.ThresholdLower,
                        _parameters.ThresholdUpper,
                        breakpoints, _parameters.IsGermline, madFactor: _parameters.MadFactor);
                }

                lock (breakpointsByChr)
                {
                    breakpointsByChr[chr] = breakpoints;
                }
            })).ToList();

            Console.WriteLine("{0} Launching wavelet tasks", DateTime.Now);
            Parallel.ForEach(tasks, task => task.Invoke());
            Console.WriteLine("{0} Completed wavelet tasks", DateTime.Now);
            Console.WriteLine("{0} Segmentation results complete", DateTime.Now);
            return breakpointsByChr;
        }

        private Dictionary<string, List<int>> AdjustBreakpoints(Dictionary<string, double[]> binsByChr, SegmentationInput segmentationInput, 
            Dictionary<string, List<int>> breakpoints, Dictionary<string, int[]> vafContainingBinsByChr)
        {
            // load common CNV segments
            Dictionary<string, List<SampleGenomicBin>> commonCNVintervals = null;
            if (_parameters.CommonCNVs != null)
            {
                commonCNVintervals = CanvasCommon.Utilities.LoadBedFile(_parameters.CommonCNVs);
                CanvasCommon.Utilities.SortAndOverlapCheck(commonCNVintervals, _parameters.CommonCNVs);
            }

            foreach (string chr in binsByChr.Keys)
            {
                if (vafContainingBinsByChr?[chr] != null && vafContainingBinsByChr[chr].Length > 0)
                {
                    if (breakpoints[chr].Max() > vafContainingBinsByChr[chr].Length)
                        throw new Exception(
                            $"breakpoint {breakpoints.Max()} is larger then the zero-based size of the " +
                            $"vafToCoverageIndex '{vafContainingBinsByChr.Count - 1}'");
                    breakpoints[chr] = breakpoints[chr].Select(breakpoint => vafContainingBinsByChr[chr][breakpoint]).ToList();
                }

                if (commonCNVintervals != null && commonCNVintervals.ContainsKey(chr))
                {
                    var remappedCommonCNVintervals = SegmentationInput.RemapCommonRegions(commonCNVintervals[chr],
                        segmentationInput.StartByChr[chr], segmentationInput.EndByChr[chr]);
                    breakpoints[chr] = SegmentationInput.OverlapCommonRegions(breakpoints[chr], remappedCommonCNVintervals);
                }
            }
            return breakpoints;
        }
    }
}