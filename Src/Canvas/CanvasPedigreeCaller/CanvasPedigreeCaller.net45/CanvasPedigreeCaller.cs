﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using MathNet.Numerics.Distributions;
using System.Threading.Tasks;
using CanvasCommon;
using Combinatorics.Collections;
using Illumina.Common;
using Illumina.Common.FileSystem;
using Isas.SequencingFiles;

namespace CanvasPedigreeCaller
{
    class CanvasPedigreeCaller
    {
        #region Members
        public int QualityFilterThreshold { get; set; } = 20;
        public int DeNovoQualityFilterThreshold { get; set; } = 20;
        public PedigreeCallerParameters CallerParameters { get; set; }
        protected double MedianCoverageThreshold = 4;
        #endregion

        internal int CallVariantsInPedigree(List<string> variantFrequencyFiles, List<string> segmentFiles, string outVcfFile, string ploidyBedPath, string referenceFolder, List<string> sampleNames, string pedigreeFile)
        {
            // load files
            // initialize data structures and classes
            int fileCounter = 0;
            var kinships = ReadPedigreeFile(pedigreeFile);
            var pedigreeMembers = new LinkedList<PedigreeMember>();
            foreach (string sampleName in sampleNames)
            {
                var pedigreeMember = SetPedigreeMember(variantFrequencyFiles, segmentFiles, ploidyBedPath, sampleName, fileCounter, CallerParameters.DefaultAlleleCountThreshold);
                pedigreeMember.Kin = kinships[pedigreeMember.Name] == PedigreeMember.Kinship.Parent ?
                    PedigreeMember.Kinship.Parent : PedigreeMember.Kinship.Offspring;
                if (kinships[pedigreeMember.Name] == PedigreeMember.Kinship.Proband)
                {
                    pedigreeMember.Kin = PedigreeMember.Kinship.Proband;
                    pedigreeMembers.AddFirst(pedigreeMember);
                }
                else
                {
                    pedigreeMembers.AddLast(pedigreeMember);

                }

                fileCounter++;
            }

            var numberOfSegments = pedigreeMembers.First().Segments.Count;
            var maxCoreNumber = 30;
            var segmentIntervals = GetParallelIntervals(numberOfSegments, Math.Min(Environment.ProcessorCount, maxCoreNumber));

            var parents = GetParents(pedigreeMembers);
            var offsprings = GetChildren(pedigreeMembers);
            double[][] transitionMatrix = GetTransitionMatrix(CallerParameters.MaximumCopyNumber);
            var parentalGenotypes = GenerateParentalGenotypes(CallerParameters.MaximumCopyNumber);
            var offspringsGenotypes = new List<List<Tuple<int, int>>>(Convert.ToInt32(Math.Pow(parentalGenotypes.Count, offsprings.Count)));
            GenerateOffspringGenotypes(offspringsGenotypes, parentalGenotypes, offsprings.Count, new List<Tuple<int, int>>());
            var genotypes = GenerateGenotypeCombinations(CallerParameters.MaximumCopyNumber);

            if (offspringsGenotypes.Count > CallerParameters.MaxNumOffspringGenotypes)
            {
                offspringsGenotypes.Shuffle();
                offspringsGenotypes = offspringsGenotypes.Take(CallerParameters.MaxNumOffspringGenotypes).ToList();
            }

            foreach (PedigreeMember parent in parents)
                parent.CnModel = new CopyNumberModel(CallerParameters.MaximumCopyNumber, parent.MeanCoverage / 2.0, parent.MeanMafCoverage / 2.0, parent.MeanCoverage * 2.5, parent.MeanMafCoverage * 2.5, parent.MaxCoverage);
            foreach (PedigreeMember offspring in offsprings)
                offspring.CnModel = new CopyNumberModel(CallerParameters.MaximumCopyNumber, offspring.MeanCoverage / 2.0, offspring.MeanMafCoverage / 2.0, offspring.MeanCoverage * 2.5, offspring.MeanMafCoverage * 2.5, offspring.MaxCoverage);

            Parallel.ForEach(
                segmentIntervals,
                interval =>
                {
                    Console.WriteLine($"{DateTime.Now} Launching SPW task for segment {interval.Start} - {interval.End}");
                    var segmentIndex = 0;
                    while (segmentIndex < numberOfSegments)
                    {
                        if (segmentIndex >= interval.Start && segmentIndex <= interval.End)
                        {
                            var useCnLikelihood = GetUseCnLikelihood(pedigreeMembers, segmentIndex);
                            var copyNumberLikelihoods = MaximalCnLikelihoodWithPedigreeInfo(parents, offsprings,
                                segmentIndex, transitionMatrix, offspringsGenotypes);
                            EstimateQScoresWithPedigreeInfo(parents, offsprings, segmentIndex, copyNumberLikelihoods);
                            if (!useCnLikelihood)
                                MaximalGtLikelihoodWithPedigreeInfo(parents, offsprings, segmentIndex, genotypes);
                        }

                        segmentIndex++;
                    }
                    Console.WriteLine($"{DateTime.Now} Finished SPW task for segment {interval.Start} - {interval.End}");
                });

            List<double?> coverage;
            List<List<CanvasSegment>> segments;
            var names = PostProcessData(outVcfFile, referenceFolder, pedigreeMembers, out coverage, out segments);
            var ploidies = pedigreeMembers.Select(x => x.Ploidy).ToList();
            CanvasSegmentWriter.WriteMultiSampleSegments(outVcfFile, segments, coverage, referenceFolder, names, null, ploidies,
            QualityFilterThreshold, isPedigreeInfoSupplied: true, denovoQualityThreshold: DeNovoQualityFilterThreshold);

            var outputFolder = new FileLocation(outVcfFile).Directory;
            foreach (var pedigreeMember in pedigreeMembers)
            {
                var outputVcfPath = SingleSampleCallset.GetSingleSamplePedigreeVcfOutput(outputFolder, pedigreeMember.Name);
                CanvasSegmentWriter.WriteSegments(outputVcfPath.FullName, pedigreeMember.Segments, pedigreeMember.MeanCoverage, referenceFolder,
                    pedigreeMember.Name, null, pedigreeMember.Ploidy, QualityFilterThreshold, isPedigreeInfoSupplied: true,
                    denovoQualityThreshold: DeNovoQualityFilterThreshold);
            }
            return 0;
        }

        private bool GetUseCnLikelihood(LinkedList<PedigreeMember> pedigreeMembers, int segmentIndex)
        {
            var copyNumberCoverageOneAndHalfNormalizer = 0.75;
            var alleles = pedigreeMembers.Select(x => x.Segments[segmentIndex].Alleles.Counts).ToList();
            var alleleCounts = alleles.Select(allele => allele.Count).ToList();
            var lowAlleleCounts = alleleCounts.Select(x => x < CallerParameters.DefaultAlleleCountThreshold).Any(c => c == true);
            var coverageCounts = pedigreeMembers.Select(x => x.Segments[segmentIndex].MedianCount).ToList();
            var isSkewedHetHomRatio = false;
            if (!lowAlleleCounts)
            {
                var minorMeanMAF = alleles.Select(allele => allele.Select(y => Math.Min(y.Item1, y.Item2)).ToList().Average());
                var minorMedianMAF = pedigreeMembers.Select(x => x.Segments[segmentIndex].Alleles.MedianCounts.Item2);
                var isHighCoverageCounts = pedigreeMembers.Select(x => x.MeanCoverage).Zip(coverageCounts,
                    (meanCoverage, segmenCoverage) => segmenCoverage > meanCoverage * copyNumberCoverageOneAndHalfNormalizer);
                var zip = minorMeanMAF.Zip(minorMedianMAF, (mn, md) => new { mn, md }).Zip(isHighCoverageCounts, (t, cov) =>
                    new { meanMAF = t.mn, medianMAF = t.md, isHighCoverage = cov });
                isSkewedHetHomRatio = zip.Any(sample => sample.meanMAF > sample.medianMAF && sample.medianMAF == 0 &&
                    sample.isHighCoverage);
            }
            var alleleDensity = pedigreeMembers.First().Segments[segmentIndex].Length / Math.Max(alleleCounts.Average(), 1.0);
            var useCnLikelihood = lowAlleleCounts ||
                alleleDensity < CallerParameters.DefaultAlleleDensityThreshold ||
                alleleCounts.Any(x => x > CallerParameters.DefaultPerSegmentAlleleMaxCounts) ||
                coverageCounts.Any(coverage => coverage < MedianCoverageThreshold) || isSkewedHetHomRatio;
            return lowAlleleCounts;
        }

        private List<string> PostProcessData(string outVcfFile, string referenceFolder, LinkedList<PedigreeMember> pedigreeMembers, out List<double?> coverage,
            out List<List<CanvasSegment>> segments)
        {
            MergeSegments(pedigreeMembers, CallerParameters.MinimumCallSize);
            var names = pedigreeMembers.Select(x => x.Name).ToList();
            coverage = pedigreeMembers.Select(x => (double?)x.MeanCoverage).ToList();
            segments = pedigreeMembers.Select(x => x.Segments).ToList();

            var outputFolder = new FileLocation(outVcfFile).Directory;
            foreach (var member in pedigreeMembers)
            {
                var coverageOutputPath = SingleSampleCallset.GetCoverageAndVariantFrequencyOutput(outputFolder, member.Name);
                CanvasSegment.WriteCoveragePlotData(member.Segments, member.MeanCoverage, member.Ploidy,
                    coverageOutputPath.FullName, referenceFolder);
            }
            return names;
        }

        internal int CallVariants(List<string> variantFrequencyFiles, List<string> segmentFiles, string outVcfFile, string ploidyBedPath, string referenceFolder, List<string> sampleNames)
        {
            // load files
            // initialize data structures and classes
            var fileCounter = 0;
            var pedigreeMembers = new LinkedList<PedigreeMember>();
            foreach (string sampleName in sampleNames)
            {
                var pedigreeMember = SetPedigreeMember(variantFrequencyFiles, segmentFiles, ploidyBedPath, sampleName, fileCounter, CallerParameters.DefaultAlleleCountThreshold);
                pedigreeMembers.AddLast(pedigreeMember);
                fileCounter++;
            }

            int numberOfSegments = pedigreeMembers.First().Segments.Count;
            const int maxCoreNumber = 30;
            var segmentIntervals = GetParallelIntervals(numberOfSegments, Math.Min(Environment.ProcessorCount, maxCoreNumber));
            var genotypes = GenerateGenotypeCombinations(CallerParameters.MaximumCopyNumber);
            var copyNumberCombinations = GenerateCopyNumberCombinations(CallerParameters.MaximumCopyNumber, CallerParameters.MaxAlleleNumber);

            foreach (PedigreeMember pedigreeMember in pedigreeMembers)
                pedigreeMember.CnModel = new CopyNumberModel(CallerParameters.MaximumCopyNumber, pedigreeMember.MeanCoverage / 2.0, pedigreeMember.MeanMafCoverage / 2.0,
                    pedigreeMember.Variance, pedigreeMember.MafVariance, pedigreeMember.MaxCoverage);

            Parallel.ForEach(
                segmentIntervals,
                interval =>
                {
                    Console.WriteLine($"{DateTime.Now} Launching SPW task for segment {interval.Start} - {interval.End}");
                    var segmentIndex = 0;
                    while (segmentIndex < numberOfSegments)
                    {
                        if (segmentIndex >= interval.Start && segmentIndex <= interval.End)
                        {
                            var useCnLikelihood = GetUseCnLikelihood(pedigreeMembers, segmentIndex);
                            var copyNumberLikelihoods = MaximalCnLikelihoodNoPedigreeInfo(pedigreeMembers, segmentIndex, copyNumberCombinations);
                            EstimateQScoresNoPedigreeInfo(pedigreeMembers, segmentIndex, copyNumberLikelihoods);
                            if (!useCnLikelihood)
                                MaximalGtLikelihoodNoPedigreeInfo(pedigreeMembers, segmentIndex, genotypes);
                        }
                        segmentIndex++;
                    }
                    Console.WriteLine($"{DateTime.Now} Finished SPW task for segment {interval.Start} - {interval.End}");
                });

            List<double?> coverage;
            List<List<CanvasSegment>> segments;
            var names = PostProcessData(outVcfFile, referenceFolder, pedigreeMembers, out coverage, out segments);
            var ploidies = pedigreeMembers.Select(x => x.Ploidy).ToList();

            CanvasSegmentWriter.WriteMultiSampleSegments(outVcfFile, segments, coverage, referenceFolder, names, null, ploidies,
            QualityFilterThreshold, isPedigreeInfoSupplied: false);

            var outputFolder = new FileLocation(outVcfFile).Directory;
            foreach (var pedigreeMember in pedigreeMembers)
            {
                var outputVcfPath = SingleSampleCallset.GetSingleSamplePedigreeVcfOutput(outputFolder, pedigreeMember.Name);
                CanvasSegmentWriter.WriteSegments(outputVcfPath.FullName, pedigreeMember.Segments, pedigreeMember.MeanCoverage, referenceFolder,
                    pedigreeMember.Name, null, pedigreeMember.Ploidy, QualityFilterThreshold, isPedigreeInfoSupplied: false);
            }
            return 0;
        }

        private static void MergeSegments(LinkedList<PedigreeMember> pedigreeMembers, int minimumCallSize)
        {
            int nSegments = pedigreeMembers.First().Segments.Count;
            var copyNumbers = new List<List<int>>(nSegments);
            var qscores = new List<double>(nSegments);
            foreach (int segmentIndex in Enumerable.Range(0, nSegments))
            {
                copyNumbers.Add(new List<int>());
                qscores.Add(0);
                foreach (var pedigreeMember in pedigreeMembers)
                {
                    copyNumbers[segmentIndex].Add(pedigreeMember.Segments[segmentIndex].CopyNumber);
                    qscores[segmentIndex] += pedigreeMember.Segments[segmentIndex].QScore;
                }
                qscores[segmentIndex] /= pedigreeMembers.Count;
            }

            if (copyNumbers == null && qscores != null || copyNumbers != null & qscores == null)
                throw new ArgumentException("Both copyNumbers and qscores arguments must be specified.");
            if (copyNumbers != null && copyNumbers.Count != pedigreeMembers.First().Segments.Count)
                throw new ArgumentException("Length of copyNumbers list should be equal to the number of segments.");
            if (qscores != null && qscores.Count != pedigreeMembers.First().Segments.Count)
                throw new ArgumentException("Length of qscores list should be equal to the number of segments.");

            foreach (var pedigreeMember in pedigreeMembers)
                CanvasSegment.MergeSegments(ref pedigreeMember.Segments, minimumCallSize, 10000, copyNumbers, qscores);
        }

        private static PedigreeMember SetPedigreeMember(List<string> variantFrequencyFiles, List<string> segmentFiles, string ploidyBedPath,
            string sampleName, int fileCounter, int defaultAlleleCountThreshold)
        {
            var pedigreeMember = new PedigreeMember
            {
                Name = sampleName,
                Segments = CanvasSegment.ReadSegments(segmentFiles[fileCounter])
            };
            pedigreeMember.MeanMafCoverage = CanvasIO.LoadFrequencies(variantFrequencyFiles[fileCounter],
                pedigreeMember.Segments);
            foreach (var segment in pedigreeMember.Segments)
                if (segment.Alleles.Counts.Count > defaultAlleleCountThreshold)
                    segment.Alleles.SetMedianCounts();
            pedigreeMember.Variance = Math.Pow(Utilities.StandardDeviation(pedigreeMember.Segments.Select(x => x.MedianCount).ToArray()), 2);
            pedigreeMember.MafVariance =
                Math.Pow(
                    Utilities.StandardDeviation(
                        pedigreeMember.Segments.Where(x => x.Alleles.TotalCoverage.Count > 0)
                            .Select(x => x.Alleles.TotalCoverage.Average())
                            .ToArray()), 2);
            pedigreeMember.MeanCoverage = pedigreeMember.Segments.Any() ? pedigreeMember.Segments.Select(x => x.MedianCount).Average() : 0;
            pedigreeMember.MaxCoverage = pedigreeMember.Segments.Any() ? (int)(pedigreeMember.Segments.Select(x => x.MedianCount).Max() + 10) : 0;
            if (!ploidyBedPath.IsNullOrEmpty() && File.Exists(ploidyBedPath))
                pedigreeMember.Ploidy = PloidyInfo.LoadPloidyFromVcfFile(ploidyBedPath, pedigreeMember.Name);
            return pedigreeMember;
        }

        private void EstimateQScoresWithPedigreeInfo(List<PedigreeMember> parents, List<PedigreeMember> offsprings, int segmentIndex,
            CopyNumberDistribution copyNumberLikelihoods)
        {
            var cnStates = GetCnStates(parents, offsprings, segmentIndex, CallerParameters.MaximumCopyNumber);
            var names = parents.Concat(offsprings).Select(x => x.Name).ToList();
            var probands = GetProbands(offsprings);
            var singleSampleQualityScores = GetSingleSampleQualityScores(copyNumberLikelihoods, cnStates, names);

            var parent1Index = names.IndexOf(parents.First().Name);
            var parent2Index = names.IndexOf(parents.Last().Name);

            foreach (PedigreeMember proband in probands)
            {
                var probandIndex = names.IndexOf(proband.Name);
                var remainingProbandIndex = probands.Except(proband.ToSingleItemEnumerable()).Select(x => names.IndexOf(x.Name));

                if (cnStates[probandIndex] != 2 && cnStates[parent1Index] == 2 && cnStates[parent2Index] == 2 &&
                    remainingProbandIndex.All(index => cnStates[index] == 2) && singleSampleQualityScores[probandIndex] > QualityFilterThreshold &&
                    singleSampleQualityScores[parent1Index] > QualityFilterThreshold && singleSampleQualityScores[parent2Index] > QualityFilterThreshold)
                {
                    var deNovoQualityScore = GetConditionalDeNovoQualityScore(copyNumberLikelihoods, probandIndex,
                        cnStates[probandIndex], names[probandIndex], parent1Index, parent2Index, remainingProbandIndex.ToList());
                    if (Double.IsInfinity(deNovoQualityScore) | deNovoQualityScore > CallerParameters.MaxQscore)
                        deNovoQualityScore = CallerParameters.MaxQscore;
                    proband.Segments[segmentIndex].DQScore = deNovoQualityScore;
                }
            }

            var counter = 0;
            foreach (PedigreeMember sample in parents.Concat(offsprings))
            {
                sample.Segments[segmentIndex].QScore = singleSampleQualityScores[counter];
                if (sample.Segments[segmentIndex].QScore < QualityFilterThreshold)
                    sample.Segments[segmentIndex].Filter = $"q{QualityFilterThreshold}";
                counter++;
            }
        }

        private void EstimateQScoresNoPedigreeInfo(LinkedList<PedigreeMember> samples, int segmentIndex, double[][] copyNumberLikelihoods)
        {
            var cnStates = samples.Select(x => Math.Min(x.Segments[segmentIndex].CopyNumber, CallerParameters.MaximumCopyNumber - 1)).ToList();
            int counter = 0;
            foreach (PedigreeMember sample in samples)
            {
                double normalizationConstant = copyNumberLikelihoods[counter].Sum();
                var qscore = -10.0 * Math.Log10((normalizationConstant - copyNumberLikelihoods[counter][cnStates[counter]]) / normalizationConstant);
                if (Double.IsInfinity(qscore) | qscore > CallerParameters.MaxQscore)
                    qscore = CallerParameters.MaxQscore;
                sample.Segments[segmentIndex].QScore = qscore;
                if (sample.Segments[segmentIndex].QScore < QualityFilterThreshold)
                    sample.Segments[segmentIndex].Filter = $"q{QualityFilterThreshold}";
                counter++;
            }
        }


        private static List<int> GetCnStates(IEnumerable<PedigreeMember> parents, IEnumerable<PedigreeMember> offsprings, int segmentIndex, int maximumCopyNumber)
        {
            return parents.Concat(offsprings).Select(x => Math.Min(x.Segments[segmentIndex].CopyNumber, maximumCopyNumber - 1)).ToList();
        }

        private static List<PedigreeMember> GetChildren(IEnumerable<PedigreeMember> pedigreeMembers)
        {
            return pedigreeMembers.Select(x => x).Where(x => x.Kin != PedigreeMember.Kinship.Parent).ToList();
        }

        private static List<PedigreeMember> GetParents(IEnumerable<PedigreeMember> pedigreeMembers)
        {
            return pedigreeMembers.Select(x => x).Where(x => x.Kin == PedigreeMember.Kinship.Parent).ToList();
        }

        private static List<PedigreeMember> GetProbands(IEnumerable<PedigreeMember> pedigreeMembers)
        {
            return pedigreeMembers.Select(x => x).Where(x => x.Kin == PedigreeMember.Kinship.Proband).ToList();
        }

        public static int AggregateVariantCoverage(ref List<CanvasSegment> segments)
        {
            var variantCoverage = segments.SelectMany(segment => segment.Alleles.TotalCoverage).ToList();
            return variantCoverage.Any() ? Utilities.Median(variantCoverage) : 0;
        }

        public double[][] GetTransitionMatrix(int numCnStates)
        {
            double[][] transitionMatrix = Utilities.MatrixCreate(numCnStates, numCnStates);
            transitionMatrix[0][0] = 1.0;
            for (int gt = 1; gt < numCnStates; gt++)
                transitionMatrix[0][gt] = 0;

            for (int cn = 1; cn < numCnStates; cn++)
            {
                var gtLikelihood = new Poisson(Math.Max(cn / 2.0, 0.1));
                for (int gt = 0; gt < numCnStates; gt++)
                    transitionMatrix[cn][gt] = gtLikelihood.Probability(gt);
            }
            return transitionMatrix;
        }

        /// <summary>
        /// Calculates maximal likelihood for copy numbers. Updated CanvasSegment CopyNumber only. 
        /// </summary>
        /// <param name="parents"></param>
        /// <param name="children"></param>
        /// <param name="segmentPosition"></param>
        /// <param name="transitionMatrix"></param>
        public CopyNumberDistribution MaximalCnLikelihoodWithPedigreeInfo(List<PedigreeMember> parents, List<PedigreeMember> children, int segmentPosition, double[][] transitionMatrix, List<List<Tuple<int, int>>> offspringsGenotypes)
        {
            double maximalLikelihood;
            InitializeLikelihood(out maximalLikelihood, segmentPosition, parents, children);
            var parent1Likelihood = parents.First().CnModel.GetCnLikelihood(Math.Min(parents.First().GetCoverage(segmentPosition), parents.First().MeanCoverage * 3.0));
            var parent2Likelihood = parents.Last().CnModel.GetCnLikelihood(Math.Min(parents.Last().GetCoverage(segmentPosition), parents.Last().MeanCoverage * 3.0));

            if (parent1Likelihood.Count != parent2Likelihood.Count)
                throw new ArgumentException("Both parents should have the same number of CN states");
            int nCopies = CallerParameters.MaximumCopyNumber;
            var names = parents.Select(x => x.Name).Union(children.Select(x => x.Name)).ToList();
            var density = new CopyNumberDistribution(nCopies, names);

            for (int cn1 = 0; cn1 < nCopies; cn1++)
            {
                for (int cn2 = 0; cn2 < nCopies; cn2++)
                {
                    foreach (var offspringGtStates in offspringsGenotypes)
                    {
                        double currentLikelihood = parent1Likelihood[cn1] * parent2Likelihood[cn2];
                        int counter = 0;
                        foreach (PedigreeMember child in children)
                        {
                            var modelIndex = Math.Min(offspringGtStates[counter].Item1 + offspringGtStates[counter].Item2, CallerParameters.MaximumCopyNumber - 1);
                            currentLikelihood *= transitionMatrix[cn1][offspringGtStates[counter].Item1] *
                                transitionMatrix[cn2][offspringGtStates[counter].Item2] *
                                child.CnModel.GetCnLikelihood(child.GetCoverage(segmentPosition))[modelIndex];
                            counter++;

                        }
                        int[] copyNumberIndices = { cn1, cn2 };
                        var index = copyNumberIndices.Concat(offspringGtStates.Select(x => x.Item1 + x.Item2)).ToArray();
                        density.SetJointProbability(Math.Max(currentLikelihood, density.GetJointProbability(index)), index);

                        currentLikelihood = Double.IsNaN(currentLikelihood) || Double.IsInfinity(currentLikelihood) ? 0 : currentLikelihood;

                        if (currentLikelihood > maximalLikelihood)
                        {
                            maximalLikelihood = currentLikelihood;
                            parents.First().Segments[segmentPosition].CopyNumber = cn1;
                            parents.Last().Segments[segmentPosition].CopyNumber = cn2;
                            counter = 0;
                            foreach (PedigreeMember child in children)
                            {
                                child.Segments[segmentPosition].CopyNumber = offspringGtStates[counter].Item1 + offspringGtStates[counter].Item2;
                                counter++;
                            }
                        }
                    }
                }
            }
            return density;
        }

        /// <summary>
        /// Calculates maximal likelihood for genotypes given a copy number call. Updated MajorChromosomeCount.
        /// </summary>
        /// <param name="parents"></param>
        /// <param name="children"></param>
        /// <param name="segmentPosition"></param>
        public void MaximalGtLikelihoodWithPedigreeInfo(List<PedigreeMember> parents, List<PedigreeMember> children, int segmentPosition, Dictionary<int, List<Tuple<int, int>>> genotypes)
        {
            double maximalLikelihood = Double.MinValue;
            int parent1CopyNumber = parents.First().Segments[segmentPosition].CopyNumber;
            int parent2CopyNumber = parents.Last().Segments[segmentPosition].CopyNumber;

            foreach (var parent1GtStates in genotypes[parent1CopyNumber])
            {
                foreach (var parent2GtStates in genotypes[parent2CopyNumber])
                {
                    var counter = 0;
                    var bestChildGtStates = new List<Tuple<int, int>>();
                    double currentLikelihood = 1;
                    foreach (PedigreeMember child in children)
                    {
                        int childCopyNumber = child.Segments[segmentPosition].CopyNumber;
                        bool isInheritedCnv = !child.Segments[segmentPosition].DQScore.HasValue;
                        double bestLikelihood = Double.MinValue;
                        Tuple<int, int> bestGtState = null;
                        foreach (var childGtState in genotypes[childCopyNumber])
                        {
                            double currentChildLikelihood;
                            if (IsGtPedigreeConcistent(parent1GtStates, childGtState) && IsGtPedigreeConcistent(parent1GtStates, childGtState)
                                && isInheritedCnv)
                                currentChildLikelihood = child.CnModel.GetCurrentGtLikelihood(child.MaxCoverage,
                                    child.GetAlleleCounts(segmentPosition), childGtState);
                            else
                                continue;
                            if (currentChildLikelihood > bestLikelihood)
                            {
                                bestLikelihood = currentChildLikelihood;
                                bestGtState = childGtState;
                            }

                        }
                        bestChildGtStates.Add(bestGtState);
                        currentLikelihood *= bestLikelihood;
                        counter++;
                    }
                    currentLikelihood *= GetCurrentGtLikelihood(parents.First(), segmentPosition, parent1GtStates) *
                                     GetCurrentGtLikelihood(parents.Last(), segmentPosition, parent2GtStates);

                    currentLikelihood = Double.IsNaN(currentLikelihood) || Double.IsInfinity(currentLikelihood) ? 0 : currentLikelihood;

                    if (currentLikelihood > maximalLikelihood)
                    {
                        maximalLikelihood = currentLikelihood;
                        if (parent1CopyNumber > 2)
                        {
                            parents.First().Segments[segmentPosition].MajorChromosomeCount =
                                Math.Max(parent1GtStates.Item1, parent1GtStates.Item2);
                            int? selectedGtState = genotypes[parent1CopyNumber].IndexOf(parent1GtStates);
                            parents.First().Segments[segmentPosition].MajorChromosomeCountScore =
                                parents.First()
                                    .CnModel.GetGtLikelihoodScore(parents.First().GetAlleleCounts(segmentPosition),
                                        genotypes[parent1CopyNumber], ref selectedGtState, parents.First().MaxCoverage);
                        }
                        else
                        {
                            parents.First().Segments[segmentPosition].MajorChromosomeCount = parent1CopyNumber;
                            parents.First().Segments[segmentPosition].MajorChromosomeCountScore = null;
                        }

                        if (parent2CopyNumber > 2)
                        {
                            parents.Last().Segments[segmentPosition].MajorChromosomeCount =
                                Math.Max(parent2GtStates.Item1, parent2GtStates.Item2);
                            int? selectedGtState = genotypes[parent2CopyNumber].IndexOf(parent2GtStates);
                            parents.Last().Segments[segmentPosition].MajorChromosomeCountScore =
                                parents.Last()
                                    .CnModel.GetGtLikelihoodScore(parents.Last().GetAlleleCounts(segmentPosition),
                                        genotypes[parent2CopyNumber], ref selectedGtState, parents.Last().MaxCoverage);
                        }
                        else
                        {
                            parents.Last().Segments[segmentPosition].MajorChromosomeCount = parent2CopyNumber;
                            parents.Last().Segments[segmentPosition].MajorChromosomeCountScore = null;
                        }

                        counter = 0;
                        foreach (PedigreeMember child in children)
                        {
                            if (bestChildGtStates[counter] != null)
                            {
                                int childCopyNumber = child.Segments[segmentPosition].CopyNumber;
                                if (childCopyNumber > 2)
                                {
                                    child.Segments[segmentPosition].MajorChromosomeCount =
                                        Math.Max(bestChildGtStates[counter].Item1, bestChildGtStates[counter].Item2);
                                    int? selectedGtState = genotypes[childCopyNumber].IndexOf(bestChildGtStates[counter]);
                                    child.Segments[segmentPosition].MajorChromosomeCountScore =
                                        child.CnModel.GetGtLikelihoodScore(child.GetAlleleCounts(segmentPosition),
                                            genotypes[childCopyNumber], ref selectedGtState, child.MaxCoverage);
                                }
                                else
                                {
                                    child.Segments[segmentPosition].MajorChromosomeCount = childCopyNumber;
                                    child.Segments[segmentPosition].MajorChromosomeCountScore = null;
                                }
                            }
                            counter++;
                        }
                    }
                }
            }
        }

        private static double GetCurrentGtLikelihood(PedigreeMember pedigreeMember, int segmentPosition, Tuple<int, int> parent1GtStates)
        {
            double currentLikelihood = pedigreeMember.CnModel.GetCurrentGtLikelihood(pedigreeMember.MaxCoverage,
                pedigreeMember.GetAlleleCounts(segmentPosition), parent1GtStates);
            return currentLikelihood;
        }

        public bool IsGtPedigreeConcistent(Tuple<int, int> parentGtStates, Tuple<int, int> childGtStates)
        {
            if (parentGtStates.Item1 == childGtStates.Item1 || parentGtStates.Item2 == childGtStates.Item1 ||
                parentGtStates.Item1 == childGtStates.Item2 || parentGtStates.Item2 == childGtStates.Item2)
                return true;
            return false;
        }

        /// <summary>
        /// Calculates maximal likelihood for segments without SNV allele ratios. Updated CanvasSegment CopyNumber only. 
        /// </summary>
        /// <param name="samples"></param>
        /// <param name="segmentPosition"></param>
        /// <param name="copyNumberCombinations"></param>
        public double[][] MaximalCnLikelihoodNoPedigreeInfo(LinkedList<PedigreeMember> samples, int segmentPosition, List<List<int>> copyNumberCombinations)
        {
            int defaultCn = 2;

            double maximalLikelihood = 0;
            foreach (PedigreeMember sample in samples)
                sample.Segments[segmentPosition].CopyNumber = defaultCn;
            int nCopies = CallerParameters.MaximumCopyNumber;
            var names = samples.Select(x => x.Name).ToList();
            var totalLikelihoods = new List<double>();
            foreach (var copyNumberCombination in copyNumberCombinations)
            {
                double totalLikelihood = 0;
                foreach (PedigreeMember sample in samples)
                {
                    maximalLikelihood = 0;
                    foreach (var copyNumber in copyNumberCombination)
                    {
                        var currentLikelihood = sample.CnModel.GetCnLikelihood(Math.Min(sample.GetCoverage(segmentPosition), sample.MeanCoverage * 3.0))[copyNumber];
                        currentLikelihood = Double.IsNaN(currentLikelihood) || Double.IsInfinity(currentLikelihood) ? 0 : currentLikelihood;
                        if (currentLikelihood > maximalLikelihood)
                            maximalLikelihood = currentLikelihood;
                    }
                    totalLikelihood += maximalLikelihood;
                }
                totalLikelihoods.Add(totalLikelihood);
            }

            var bestcopyNumberCombination = copyNumberCombinations[totalLikelihoods.IndexOf(totalLikelihoods.Max())];
            int counter = 0;
            var density = new double[samples.Count][];
            foreach (PedigreeMember sample in samples)
            {
                maximalLikelihood = 0;
                density[counter] = new double[nCopies];
                counter++;
                foreach (var copyNumber in bestcopyNumberCombination)
                {
                    var currentLikelihood = sample.CnModel.GetCnLikelihood(Math.Min(sample.GetCoverage(segmentPosition), sample.MeanCoverage * 3.0))[copyNumber];
                    currentLikelihood = Double.IsNaN(currentLikelihood) || Double.IsInfinity(currentLikelihood) ? 0 : currentLikelihood;
                    if (currentLikelihood > maximalLikelihood)
                    {
                        maximalLikelihood = currentLikelihood;
                        sample.Segments[segmentPosition].CopyNumber = copyNumber;
                        density[names.FindIndex(name => name == sample.Name)][copyNumber] = maximalLikelihood;
                    }
                }
            }
            return density;
        }

        /// <summary>
        /// Calculates maximal likelihood for segments with SNV allele counts given CopyNumber. Updated MajorChromosomeCount.
        /// </summary>
        /// <param name="samples"></param>
        /// <param name="segmentPosition"></param>
        /// <param name="genotypes"></param>       
        public void MaximalGtLikelihoodNoPedigreeInfo(LinkedList<PedigreeMember> samples, int segmentPosition, Dictionary<int, List<Tuple<int, int>>> genotypes)
        {
            foreach (PedigreeMember sample in samples)
            {
                int copyNumber = sample.Segments[segmentPosition].CopyNumber;
                if (copyNumber > 2)
                {
                    sample.Segments[segmentPosition].MajorChromosomeCount = copyNumber;
                    return;
                }
                var genotypeset = genotypes[copyNumber];
                int? selectedGtState = null;
                double gqscore = sample.CnModel.GetGtLikelihoodScore(sample.GetAlleleCounts(segmentPosition),
                    genotypeset, ref selectedGtState, sample.MaxCoverage);
                sample.Segments[segmentPosition].MajorChromosomeCountScore = gqscore;
                sample.Segments[segmentPosition].MajorChromosomeCount = Math.Max(genotypeset[selectedGtState.Value].Item1,
                    genotypeset[selectedGtState.Value].Item2);
            }
        }


        private static void InitializeLikelihood(out double maximalLikelihood, int segmentPosition, List<PedigreeMember> parents, List<PedigreeMember> children)
        {
            maximalLikelihood = 0;
            int defaultCn = 2;

            parents.First().Segments[segmentPosition].CopyNumber = defaultCn;
            parents.Last().Segments[segmentPosition].CopyNumber = defaultCn;
            foreach (PedigreeMember child in children)
                child.Segments[segmentPosition].CopyNumber = defaultCn;
        }

        /// <summary>
        /// Generate all possible copy number combinations with the maximal number of copy numbers per segment set to maxAlleleNumber.
        /// </summary>
        /// <param name="numberOfCnStates"></param>
        /// <param name="maxAlleleNumber"></param>
        /// <returns></returns>
        public static List<List<int>> GenerateCopyNumberCombinations(int numberOfCnStates, int maxAlleleNumber)
        {
            if (numberOfCnStates <= 0)
                throw new ArgumentOutOfRangeException(nameof(numberOfCnStates));
            var cnStates = Enumerable.Range(0, numberOfCnStates).ToList();
            var allCombinations = new List<List<int>>();
            for (int currentAlleleNumber = 1; currentAlleleNumber <= maxAlleleNumber; currentAlleleNumber++)
            {
                var permutations = new Combinations<int>(cnStates, currentAlleleNumber);
                var list = permutations.Select(x => x.ToList()).ToList();
                allCombinations.AddRange(list);
            }
            return allCombinations;
        }

        public List<Tuple<int, int>> GenerateParentalGenotypes(int numCnStates)
        {
            var genotypes = new List<Tuple<int, int>>();
            for (int cn = 0; cn < numCnStates; cn++)
            {
                for (int gt = 0; gt <= cn; gt++)
                {
                    genotypes.Add(new Tuple<int, int>(gt, cn - gt));
                }
            }
            return genotypes;
        }

        /// <summary>
        /// Generate all possible copy number genotype combinations with the maximal number of alleles per segment set to maxAlleleNumber.
        /// </summary>
        /// <param name="numberOfCnStates"></param>
        /// <returns> </returns>
        public List<Tuple<int, int>> GenerateGenotypes(int numberOfCnStates)
        {
            var genotypes = new List<Tuple<int, int>>();
            for (int gt = 0; gt <= numberOfCnStates; gt++)
            {
                genotypes.Add(new Tuple<int, int>(gt, numberOfCnStates - gt));
            }
            return genotypes;
        }

        /// <summary>
        /// Generate all possible copy number genotype combinations with the maximal number of alleles per segment set to maxAlleleNumber.
        /// </summary>
        /// <param name="numberOfCnStates"></param>
        /// <returns> </returns>
        public Dictionary<int, List<Tuple<int, int>>> GenerateGenotypeCombinations(int numberOfCnStates)
        {
            var genotypes = new Dictionary<int, List<Tuple<int, int>>>();
            for (int cn = 0; cn < numberOfCnStates; cn++)
            {
                genotypes[cn] = new List<Tuple<int, int>>();
                for (int gt = 0; gt <= cn; gt++)
                {
                    genotypes[cn].Add(new Tuple<int, int>(gt, cn - gt));
                }
            }
            return genotypes;
        }

        public void GenerateOffspringGenotypes(List<List<Tuple<int, int>>> offspringGenotypes, List<Tuple<int, int>> genotypeSet, int nOffsprings, List<Tuple<int, int>> partialGenotypes)
        {

            if (nOffsprings > 0)
            {
                foreach (Tuple<int, int> genotype in genotypeSet)
                {
                    GenerateOffspringGenotypes(offspringGenotypes, genotypeSet, nOffsprings - 1, partialGenotypes.Concat(new List<Tuple<int, int>> { genotype }).ToList());
                }
            }
            if (nOffsprings == 0)
            {
                offspringGenotypes.Add(partialGenotypes);
            }
        }

        private List<GenomicInterval> GetParallelIntervals(int nSegments, int nCores)
        {
            var intervals = new List<GenomicInterval>();
            //intervals.Add(new GenomicInterval(17231, 34515));

            int step = nSegments / nCores;
            intervals.Add(new GenomicInterval(0, step));
            int cumSum = step + 1;
            while (cumSum + step + 1 < nSegments - 1)
            {
                intervals.Add(new GenomicInterval(cumSum, cumSum + step));
                cumSum += step + 1;
            }
            intervals.Add(new GenomicInterval(cumSum, nSegments - 1));
            return intervals;
        }

        public double GetTransitionProbability(int gt1Parent, int gt2Parent, int gt1Offspring, int gt2Offspring)
        {
            if (gt1Parent == gt1Offspring || gt1Parent == gt2Offspring ||
                gt2Parent == gt1Offspring || gt2Parent == gt2Offspring)
                return 0.5;
            return CallerParameters.DeNovoRate;
        }


        public Dictionary<string, PedigreeMember.Kinship> ReadPedigreeFile(string pedigreeFile)
        {
            Dictionary<string, PedigreeMember.Kinship> kinships = new Dictionary<string, PedigreeMember.Kinship>();
            using (FileStream stream = new FileStream(pedigreeFile, FileMode.Open, FileAccess.Read))
            using (StreamReader reader = new StreamReader(stream))
            {
                string row;
                while ((row = reader.ReadLine()) != null)
                {
                    string[] fields = row.Split('\t');
                    string maternalId = fields[2];
                    string paternallId = fields[3];
                    string proband = fields[5];
                    if (maternalId == "0" && paternallId == "0")
                        kinships.Add(fields[1], PedigreeMember.Kinship.Parent);
                    else if (proband == "affected")
                        kinships.Add(fields[1], PedigreeMember.Kinship.Proband);
                    else
                        kinships.Add(fields[1], PedigreeMember.Kinship.Offspring);
                }
            }
            return kinships;
        }

        public List<double> GetSingleSampleQualityScores(CopyNumberDistribution density, List<int> cnStates, List<string> sampleNames)
        {
            var singleSampleQualityScores = new List<double>();
            if (density.Count != cnStates.Count)
                throw new ArgumentException("Size of CopyNumberDistribution should be equal to number of CN states");
            for (int index = 0; index < sampleNames.Count; index++)
            {
                string sampleName = sampleNames[index];
                var cnMarginalProbabilities = density.GetMarginalProbability(cnStates.Count, CallerParameters.MaximumCopyNumber, sampleName);
                double normalizationConstant = cnMarginalProbabilities.Sum();
                var qscore = -10.0 * Math.Log10((normalizationConstant - cnMarginalProbabilities[cnStates[index]]) / normalizationConstant);
                if (Double.IsInfinity(qscore) | qscore > CallerParameters.MaxQscore)
                    qscore = CallerParameters.MaxQscore;
                singleSampleQualityScores.Add(qscore);
            }
            return singleSampleQualityScores;
        }

        public double GetDeNovoQualityScore(List<PedigreeMember> parents, CopyNumberDistribution density, string sampleName, int sampleValue, double sampleProbability)
        {
            int nSamples = density.Count;
            const int diploidState = 2;
            var probandMarginalProbabilities = density.GetMarginalProbability(nSamples, CallerParameters.MaximumCopyNumber, sampleName);
            double normalization = probandMarginalProbabilities[sampleValue] + probandMarginalProbabilities[diploidState];
            double probandMarginalAlt = probandMarginalProbabilities[sampleValue] / normalization;
            //density.SetConditionalProbability(density.Count, MaximumCopyNumber, sampleName, sampleValue, probandMarginalProbabilities[sampleValue]);

            var parentNames = parents.Select(x => x.Name).ToList();
            var firstParentMarginalProbabilities = density.GetMarginalProbability(nSamples, CallerParameters.MaximumCopyNumber, parentNames.First());
            var secondParentMarginalProbabilities = density.GetMarginalProbability(nSamples, CallerParameters.MaximumCopyNumber, parentNames.Last());
            normalization = firstParentMarginalProbabilities[sampleValue] + firstParentMarginalProbabilities[diploidState];
            double firstParentMarginalAlt = Math.Min(Math.Max(firstParentMarginalProbabilities[sampleValue] / normalization, 0.001), 0.999);
            normalization = secondParentMarginalProbabilities[sampleValue] + secondParentMarginalProbabilities[diploidState];
            double secondParentMarginalAlt = Math.Min(Math.Max(secondParentMarginalProbabilities[sampleValue] / normalization, 0.001), 0.999);

            normalization = (1 - firstParentMarginalAlt) * secondParentMarginalAlt + firstParentMarginalAlt * secondParentMarginalAlt + (1 - firstParentMarginalAlt) * (1 - firstParentMarginalAlt) +
            (1 - secondParentMarginalAlt) * firstParentMarginalAlt;
            double diploidProbability = (1 - firstParentMarginalAlt) * (1 - secondParentMarginalAlt) / normalization;
            double denovoProbability = diploidProbability * probandMarginalAlt;
            double qscore = -10.0 * Math.Log10(1 - denovoProbability);
            return qscore;
        }

        public double GetConditionalDeNovoQualityScore(CopyNumberDistribution density, int probandIndex,
                    int probandCopyNumber, string probandName, int parent1Index, int parent2Index, List<int> remainingProbandIndex)
        {

            var numerator = 0.0;
            var denominator = 0.0;
            const int diploidState = 2;
            int nSamples = density.Count;
            var probandMarginalProbabilities = density.GetMarginalProbability(nSamples, CallerParameters.MaximumCopyNumber, probandName);
            double normalization = probandMarginalProbabilities[probandCopyNumber] + probandMarginalProbabilities[diploidState];
            double probandMarginalAlt = probandMarginalProbabilities[probandCopyNumber] / normalization;

            foreach (var copyNumberIndex in density.Indices.Where(x => x[probandIndex] == probandCopyNumber))
            {
                if (!(density.GetJointProbability(copyNumberIndex.ToArray()) > 0.0)) continue;

                var holder = density.GetJointProbability(copyNumberIndex);
                denominator += holder;

                if (copyNumberIndex[parent1Index] == diploidState && copyNumberIndex[parent2Index] == diploidState && remainingProbandIndex.All(index => copyNumberIndex[index] == 2))
                    numerator += holder;
            }

            const double q60 = 0.000001;
            double denovoProbability = (1 - numerator / denominator) * (1 - probandMarginalAlt);
            double qscore = -10.0 * Math.Log10(Math.Max(denovoProbability, q60));
            return qscore;
        }
    }

}
