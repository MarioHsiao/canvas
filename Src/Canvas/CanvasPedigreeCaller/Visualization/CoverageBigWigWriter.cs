using System.Collections.Generic;
using CanvasCommon;
using Illumina.Common.FileSystem;
using Isas.ClassicBioinfoTools.KentUtils;
using Isas.SequencingFiles;

namespace CanvasPedigreeCaller.Visualization
{
    public class CoverageBigWigWriterFactory
    {
        private readonly FormatConverterFactory _converterFactory;
        private readonly GenomeMetadata _genome;

        public CoverageBigWigWriterFactory(FormatConverterFactory converterFactory, GenomeMetadata genome)
        {
            _converterFactory = converterFactory;
            _genome = genome;
        }

        public ICoverageBigWigWriter Create()
        {
            return new CoverageBigWigWriter(new NormalizedCoverageWriterFacade(new NormalizedCoverageBedGraphWriter(new NormalizedCoverageCalculator(), 4)),
                _converterFactory.GetBedGraphToBigWigConverter(), _genome);
        }
    }

    public class CoverageBigWigWriter : ICoverageBigWigWriter
    {
        private readonly ICoverageBedGraphWriter _writer;
        private readonly BedGraphToBigWigConverter _converter;
        private readonly GenomeMetadata _genome;

        public CoverageBigWigWriter(ICoverageBedGraphWriter writer, BedGraphToBigWigConverter converter, GenomeMetadata genome)
        {
            _writer = writer;
            _converter = converter;
            _genome = genome;
        }

        public IFileLocation Write(IReadOnlyList<CanvasSegment> segments, IDirectoryLocation output)
        {
            var bedGraph = output.GetFileLocation("coverage.bedgraph");
            _writer.Write(segments, bedGraph);
            var bigWigConverterOutput = output.CreateSubdirectory("BigWigConverter");
            return _converter.Convert(bedGraph, _genome, bigWigConverterOutput);
        }
    }
}