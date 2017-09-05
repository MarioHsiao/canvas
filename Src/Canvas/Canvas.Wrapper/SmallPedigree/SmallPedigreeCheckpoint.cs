﻿using Illumina.Common.FileSystem;
using Isas.Framework.Checkpointing;
using Isas.Framework.Logging;

namespace Canvas.Wrapper.SmallPedigree
{
    public interface ISmallPedigreeCheckpoint
    {
        CanvasSmallPedigreeOutput Run(CanvasSmallPedigreeInput input, IDirectoryLocation sandbox, IFileMover fileMover);
    }

    public class SmallPedigreeCheckpoint : ISmallPedigreeCheckpoint
    {
        private readonly CanvasSmallPedigreeWrapper _wrapper;
        private readonly Move _move;
        private readonly Load _load;

        public delegate void Move(CanvasSmallPedigreeOutput source, IFileMover fileMover);

        public delegate CanvasSmallPedigreeOutput Load(CanvasSmallPedigreeInput input);

        public SmallPedigreeCheckpoint(CanvasSmallPedigreeWrapper wrapper, Move move, Load load)
        {
            _wrapper = wrapper;
            _move = move;
            _load = load;
        }

        public CanvasSmallPedigreeOutput Run(CanvasSmallPedigreeInput input, IDirectoryLocation sandbox, IFileMover fileMover)
        {
            System.Console.WriteLine($"%%% CanvasSmallPedigreeOutput::Run");
            CanvasSmallPedigreeOutput output = _wrapper.Run(input, sandbox);
            System.Console.WriteLine($"%%% CanvasSmallPedigreeOutput::Run - output is '{output}'");
            if (output == null) return null;
            _move(output, fileMover);
            return _load(input);
        }
    }

    public class NullSmallPedigreeCheckpoint : ISmallPedigreeCheckpoint
    {
        private readonly ILogger _logger;

        public NullSmallPedigreeCheckpoint(ILogger logger)
        {
            _logger = logger;
        }

        public CanvasSmallPedigreeOutput Run(CanvasSmallPedigreeInput input, IDirectoryLocation sandbox, IFileMover fileMover)
        {
            System.Console.WriteLine($"%%% NullSmallPedigreeCheckpoint::Run");
            _logger.Info("Canvas is disabled.");
            return null;
        }
    }
}