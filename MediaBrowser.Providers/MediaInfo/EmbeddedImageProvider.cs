#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Net;

namespace MediaBrowser.Providers.MediaInfo
{
    /// <summary>
    /// Uses <see cref="IMediaEncoder"/> to extract embedded images.
    /// </summary>
    public class EmbeddedImageProvider : IDynamicImageProvider, IHasOrder
    {
        private static readonly string[] _primaryImageFileNames =
        {
            "poster",
            "folder",
            "cover",
            "default"
        };

        private static readonly string[] _backdropImageFileNames =
        {
            "backdrop",
            "fanart",
            "background",
            "art"
        };

        private static readonly string[] _logoImageFileNames =
        {
            "logo",
        };

        private readonly IMediaEncoder _mediaEncoder;

        /// <summary>
        /// Initializes a new instance of the <see cref="EmbeddedImageProvider"/> class.
        /// </summary>
        /// <param name="mediaEncoder">The media encoder for extracting attached/embedded images.</param>
        public EmbeddedImageProvider(IMediaEncoder mediaEncoder)
        {
            _mediaEncoder = mediaEncoder;
        }

        /// <inheritdoc />
        public string Name => "Embedded Image Extractor";

        /// <inheritdoc />
        // Default to after internet image providers but before Screen Grabber
        public int Order => 99;

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            if (item is Video)
            {
                if (item is Episode)
                {
                    return new[]
                    {
                        ImageType.Primary,
                    };
                }

                return new[]
                {
                    ImageType.Primary,
                    ImageType.Backdrop,
                    ImageType.Logo,
                };
            }

            return Array.Empty<ImageType>();
        }

        /// <inheritdoc />
        public Task<DynamicImageResponse> GetImage(BaseItem item, ImageType type, CancellationToken cancellationToken)
        {
            var video = (Video)item;

            // No support for these
            if (video.IsPlaceHolder || video.VideoType == VideoType.Dvd)
            {
                return Task.FromResult(new DynamicImageResponse { HasImage = false });
            }

            return GetEmbeddedImage(video, type, cancellationToken);
        }

        private async Task<DynamicImageResponse> GetEmbeddedImage(Video item, ImageType type, CancellationToken cancellationToken)
        {
            MediaSourceInfo mediaSource = new MediaSourceInfo
            {
                VideoType = item.VideoType,
                IsoType = item.IsoType,
                Protocol = item.PathProtocol ?? MediaProtocol.File,
            };

            string[] imageFileNames = type switch
            {
                ImageType.Primary => _primaryImageFileNames,
                ImageType.Backdrop => _backdropImageFileNames,
                ImageType.Logo => _logoImageFileNames,
                _ => _primaryImageFileNames
            };

            // Try attachments first
            var attachmentStream = item.GetMediaSources(false)
                .SelectMany(source => source.MediaAttachments)
                .FirstOrDefault(attachment => !string.IsNullOrEmpty(attachment.FileName)
                    && imageFileNames.Any(name => attachment.FileName.Contains(name, StringComparison.OrdinalIgnoreCase)));

            if (attachmentStream != null)
            {
                return await ExtractAttachment(item, attachmentStream, mediaSource, cancellationToken);
            }

            // Fall back to EmbeddedImage streams
            var imageStreams = item.GetMediaStreams().FindAll(i => i.Type == MediaStreamType.EmbeddedImage);

            if (imageStreams.Count == 0)
            {
                // Can't extract if we don't have any EmbeddedImage streams
                return new DynamicImageResponse { HasImage = false };
            }

            // Extract first stream containing an element of imageFileNames
            var imageStream = imageStreams
                .FirstOrDefault(stream => !string.IsNullOrEmpty(stream.Comment)
                    && imageFileNames.Any(name => stream.Comment.Contains(name, StringComparison.OrdinalIgnoreCase)));

            // Primary type only: default to first image if none found by label
            if (imageStream == null)
            {
                if (type == ImageType.Primary)
                {
                    imageStream = imageStreams[0];
                }
                else
                {
                    // No streams matched, abort
                    return new DynamicImageResponse { HasImage = false };
                }
            }

            string extractedImagePath =
                await _mediaEncoder.ExtractVideoImage(item.Path, item.Container, mediaSource, imageStream, imageStream.Index, ".jpg", cancellationToken)
                    .ConfigureAwait(false);

            return new DynamicImageResponse
            {
                Format = ImageFormat.Jpg,
                HasImage = true,
                Path = extractedImagePath,
                Protocol = MediaProtocol.File
            };
        }

        private async Task<DynamicImageResponse> ExtractAttachment(Video item, MediaAttachment attachmentStream, MediaSourceInfo mediaSource, CancellationToken cancellationToken)
        {
            var extension = string.IsNullOrEmpty(attachmentStream.MimeType)
                ? Path.GetExtension(attachmentStream.FileName)
                : MimeTypes.ToExtension(attachmentStream.MimeType);

            if (string.IsNullOrEmpty(extension))
            {
                extension = ".jpg";
            }

            string extractedAttachmentPath =
                await _mediaEncoder.ExtractVideoImage(item.Path, item.Container, mediaSource, null, attachmentStream.Index, extension, cancellationToken)
                    .ConfigureAwait(false);

            ImageFormat format = extension switch
            {
                ".bmp" => ImageFormat.Bmp,
                ".gif" => ImageFormat.Gif,
                ".jpg" => ImageFormat.Jpg,
                ".png" => ImageFormat.Png,
                ".webp" => ImageFormat.Webp,
                _ => ImageFormat.Jpg
            };

            return new DynamicImageResponse
            {
                Format = format,
                HasImage = true,
                Path = extractedAttachmentPath,
                Protocol = MediaProtocol.File
            };
        }

        /// <inheritdoc />
        public bool Supports(BaseItem item)
        {
            if (item.IsShortcut)
            {
                return false;
            }

            if (!item.IsFileProtocol)
            {
                return false;
            }

            return item is Video video && !video.IsPlaceHolder && video.IsCompleteMedia;
        }
    }
}
