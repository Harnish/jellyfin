using System;

namespace MediaBrowser.Model.Entities
{
    /// <summary>
    /// Enum ImageType.
    /// </summary>
    public enum ImageType
    {
        /// <summary>
        /// The primary.
        /// </summary>
        Primary = 0,

        /// <summary>
        /// The art.
        /// </summary>
        Art = 1,

        /// <summary>
        /// The backdrop.
        /// </summary>
        Backdrop = 2,

        /// <summary>
        /// The banner.
        /// </summary>
        Banner = 3,

        /// <summary>
        /// The logo.
        /// </summary>
        Logo = 4,

        /// <summary>
        /// The thumb.
        /// </summary>
        Thumb = 5,

        /// <summary>
        /// The disc.
        /// </summary>
        Disc = 6,

        /// <summary>
        /// The box.
        /// </summary>
        Box = 7,

        /// <summary>
        /// The screenshot.
        /// </summary>
        [Obsolete("Screenshot image type is no longer used.")]
        Screenshot = 8,

        /// <summary>
        /// The menu.
        /// </summary>
        Menu = 9,

        /// <summary>
        /// The chapter image.
        /// </summary>
        Chapter = 10,

        /// <summary>
        /// The box rear.
        /// </summary>
        BoxRear = 11,

        /// <summary>
        /// The user profile image.
        /// </summary>
        Profile = 12
    }
}
