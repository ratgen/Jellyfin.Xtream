﻿// Copyright (C) 2022  Kevin Jilissen

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Jellyfin.Xtream
{
    /// <summary>
    /// The Xtream Codes API channel.
    /// </summary>
    public class LiveChannel : IChannel
    {
        private readonly ILogger<LiveChannel> logger;
        private readonly IMemoryCache memoryCache;

        /// <summary>
        /// Initializes a new instance of the <see cref="LiveChannel"/> class.
        /// </summary>
        /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
        /// <param name="memoryCache">Instance of the <see cref="IMemoryCache"/> interface.</param>
        public LiveChannel(ILogger<LiveChannel> logger, IMemoryCache memoryCache)
        {
            this.logger = logger;
            this.memoryCache = memoryCache;
        }

        /// <inheritdoc />
        public string? Name => "Xtream Live";

        /// <inheritdoc />
        public string? Description => "Live IPTV streamed from the Xtream-compatible server.";

        /// <inheritdoc />
        public string DataVersion => string.Empty;

        /// <inheritdoc />
        public string HomePageUrl => string.Empty;

        /// <inheritdoc />
        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

        /// <inheritdoc />
        public InternalChannelFeatures GetChannelFeatures()
        {
            return new InternalChannelFeatures
            {
                ContentTypes = new List<ChannelMediaContentType>
                {
                    ChannelMediaContentType.TvExtra,
                },

                MediaTypes = new List<ChannelMediaType>
                {
                    ChannelMediaType.Video
                },
                MaxPageSize = int.MaxValue
            };
        }

        /// <inheritdoc />
        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            switch (type)
            {
                default:
                    throw new ArgumentException("Unsupported image type: " + type);
            }
        }

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedChannelImages()
        {
            return new List<ImageType>
            {
                // ImageType.Primary
            };
        }

        /// <inheritdoc />
        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(query.FolderId))
            {
                return await GetCategories(cancellationToken).ConfigureAwait(false);
            }

            return await GetVideos(query.FolderId, cancellationToken).ConfigureAwait(false);
        }

        private async Task<ChannelItemResult> GetCategories(CancellationToken cancellationToken)
        {
            string key = "xtream-live-categories";
            if (memoryCache.TryGetValue(key, out ChannelItemResult o))
            {
                return o;
            }

            Plugin? plugin = Plugin.Instance;
            if (plugin == null)
            {
                throw new ArgumentException("Plugin not initialized!");
            }

            using (XtreamClient client = new XtreamClient())
            {
                List<Category> categories = await client.GetLiveCategoryAsync(plugin.Creds, cancellationToken).ConfigureAwait(false);
                List<ChannelItemInfo> items = new List<ChannelItemInfo>();

                foreach (Category category in categories)
                {
                    items.Add(new ChannelItemInfo()
                    {
                        Id = category.CategoryId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Name = category.CategoryName,
                        Type = ChannelItemType.Folder,
                    });
                }

                ChannelItemResult result = new ChannelItemResult()
                {
                    Items = items,
                    TotalRecordCount = items.Count
                };
                memoryCache.Set(key, result, DateTimeOffset.Now.AddMinutes(7));
                return result;
            }
        }

        private async Task<ChannelItemResult> GetVideos(string categoryId, CancellationToken cancellationToken)
        {
            string key = $"xtream-live-{categoryId}";
            if (memoryCache.TryGetValue(key, out ChannelItemResult o))
            {
                return o;
            }

            Plugin? plugin = Plugin.Instance;
            if (plugin == null)
            {
                throw new ArgumentException("Plugin not initialized!");
            }

            using (XtreamClient client = new XtreamClient())
            {
                var channels = await client.GetLiveStreamsByCategoryAsync(plugin.Creds, categoryId, cancellationToken).ConfigureAwait(false);
                List<ChannelItemInfo> items = new List<ChannelItemInfo>();

                foreach (var channel in channels)
                {
                    long added = long.Parse(channel.Added, System.Globalization.CultureInfo.InvariantCulture);

                    PluginConfiguration config = plugin.Configuration;
                    string uri = $"{config.BaseUrl}/{config.Username}/{config.Password}/{channel.StreamId}";
                    if (!string.IsNullOrEmpty(channel.ContainerExtension))
                    {
                        uri += $".{channel.ContainerExtension}";
                    }

                    List<MediaSourceInfo> sources = new List<MediaSourceInfo>()
                    {
                        new MediaSourceInfo()
                        {
                            EncoderProtocol = MediaBrowser.Model.MediaInfo.MediaProtocol.Http,
                            Id = "xtream-live-" + channel.StreamId,
                            IsInfiniteStream = true,
                            IsRemote = true,
                            Name = channel.Name,
                            Path = uri,
                            Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.Http,
                        }
                    };
                    items.Add(new ChannelItemInfo()
                    {
                        ContentType = ChannelMediaContentType.TvExtra,
                        DateCreated = DateTimeOffset.FromUnixTimeSeconds(added).DateTime,
                        FolderType = ChannelFolderType.Container,
                        Id = channel.StreamId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ImageUrl = channel.StreamIcon,
                        IsLiveStream = true,
                        MediaSources = sources,
                        MediaType = ChannelMediaType.Video,
                        Name = channel.Name,
                        Type = ChannelItemType.Media,
                    });
                }

                ChannelItemResult result = new ChannelItemResult()
                {
                    Items = items,
                    TotalRecordCount = items.Count
                };
                memoryCache.Set(key, result, DateTimeOffset.Now.AddMinutes(1));
                return result;
            }
        }

        /// <inheritdoc />
        public bool IsEnabledFor(string userId)
        {
            return true;
        }
    }
}