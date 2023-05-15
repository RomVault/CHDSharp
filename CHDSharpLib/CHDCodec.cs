using CHDReaderTest.Flac.FlacDeps;
using CUETools.Codecs.Flake;

namespace CHDSharpLib
{
    internal class CHDCodec
    {
        internal AudioPCMConfig FLAC_settings = null;
        internal AudioDecoder FLAC_audioDecoder = null;
        internal AudioBuffer FLAC_audioBuffer = null;


        internal AudioPCMConfig AVHUFF_settings = null;
        internal AudioDecoder AVHUFF_audioDecoder = null;
    }
}
