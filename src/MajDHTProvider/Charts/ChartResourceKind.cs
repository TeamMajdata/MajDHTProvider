#nullable enable

namespace MajDHTProvider.Charts
{
    /// <summary>Identifies one of the files MajdataPlay loads for an online chart.</summary>
    internal enum ChartResourceKind
    {
        /// <summary>The chart text, stored as <c>maidata.txt</c> and published under the <c>maidata</c> key suffix.</summary>
        Maidata,
        /// <summary>The audio track, stored as <c>track.mp3</c> and published under the <c>track</c> key suffix.</summary>
        Track,
        /// <summary>The cover image, stored as <c>bg.jpg</c> and published under the <c>image</c> key suffix.</summary>
        Image,
        /// <summary>The full-size cover image, stored as <c>bg_fullSize.jpg</c> and published under the <c>image-fullsize</c> key suffix.</summary>
        ImageFullSize,
        /// <summary>The background video, stored as <c>bg.mp4</c> and published under the <c>video</c> key suffix.</summary>
        Video
    }
}
