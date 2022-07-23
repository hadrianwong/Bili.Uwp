﻿// Copyright (c) Richasy. All rights reserved.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Bili.Models.App.Args;
using Bili.Models.App.Constants;
using Bili.Models.Enums;
using Bili.Models.Enums.App;
using FFmpegInteropX;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Web.Http;

namespace Bili.ViewModels.Uwp.Core
{
    /// <summary>
    /// 使用 FFmpeg 的播放器视图模型.
    /// </summary>
    public sealed partial class FFmpegPlayerViewModel
    {
        private HttpClient GetVideoClient()
        {
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Referer = new Uri("https://www.bilibili.com");
            httpClient.DefaultRequestHeaders.Add("User-Agent", ServiceConstants.DefaultUserAgentString);
            return httpClient;
        }

        private HttpClient GetLiveClient()
        {
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Referer = new Uri("https://live.bilibili.com/");
            httpClient.DefaultRequestHeaders.Add("rtsp_transport", "tcp");
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 BiliDroid/1.12.0 (bbcallen@gmail.com)");
            return httpClient;
        }

        private async Task LoadDashVideoSourceAsync()
        {
            var hasAudio = _audio != null;
            _videoConfig.VideoDecoderMode = GetDecoderMode();

            var tasks = new List<Task>
                {
                    Task.Run(async () =>
                    {
                        var client = GetVideoClient();
                        _videoStream = await HttpRandomAccessStream.CreateAsync(client, new Uri(_video.BaseUrl));
                        _videoFFSource = await FFmpegMediaSource.CreateFromStreamAsync(_videoStream, _videoConfig);
                    }),
                    Task.Run(async () =>
                    {
                        if (hasAudio)
                        {
                            var client = GetVideoClient();
                            _audioStream = await HttpRandomAccessStream.CreateAsync(client, new Uri(_audio.BaseUrl));
                            _audioFFSource = await FFmpegMediaSource.CreateFromStreamAsync(_audioStream, _videoConfig);
                        }
                    }),
                };

            await Task.WhenAll(tasks);
            _videoPlaybackItem = _videoFFSource.CreateMediaPlaybackItem();

            if (_videoPlayer == null)
            {
                _videoPlayer = GetVideoPlayer();
            }

            _videoPlayer.Source = _videoPlaybackItem;

            if (hasAudio)
            {
                _audioPlaybackItem = _audioFFSource.CreateMediaPlaybackItem();
                _mediaTimelineController = GetTimelineController();
                _videoPlayer.CommandManager.IsEnabled = false;
                _videoPlayer.TimelineController = _mediaTimelineController;

                if (_audioPlayer == null)
                {
                    _audioPlayer = GetVideoPlayer();
                }

                _audioPlayer.CommandManager.IsEnabled = false;
                _audioPlayer.Source = _audioPlaybackItem;
                _audioPlayer.TimelineController = _mediaTimelineController;
            }
            else
            {
                _audioPlayer = null;
            }

            MediaPlayerChanged?.Invoke(this, _videoPlayer);
        }

        private async Task LoadDashLiveSourceAsync(string url)
        {
            try
            {
                _liveConfig.VideoDecoderMode = GetDecoderMode();
                var client = GetLiveClient();

                // 这里是一种试错的机制。对于国内用户来说，可以通过 Url 直接创建播放源
                // 但是对于海外地区，直接创建播放源可能会崩，需要先获取网络流.
                if (_liveRetryCount == 0)
                {
                    _videoStream = await HttpRandomAccessStream.CreateAsync(client, new Uri(url));
                    _videoFFSource = await FFmpegMediaSource.CreateFromStreamAsync(_videoStream);
                }
                else
                {
                    _videoFFSource = await FFmpegMediaSource.CreateFromUriAsync(url, _liveConfig);
                }

                _videoPlaybackItem = _videoFFSource.CreateMediaPlaybackItem();

                if (_videoPlayer == null)
                {
                    _videoPlayer = GetVideoPlayer();
                }

                _videoPlayer.Source = _videoPlaybackItem;

                MediaPlayerChanged?.Invoke(this, _videoPlayer);
            }
            catch (Exception ex)
            {
                _liveRetryCount++;

                if (_liveRetryCount < 2)
                {
                    await LoadDashLiveSourceAsync(url);
                }
                else
                {
                    Status = PlayerStatus.Failed;
                    var msg = _resourceToolkit.GetLocaleString(LanguageNames.RequestLivePlayInformationFailed) + "\n" + ex.Message;
                    StateChanged?.Invoke(this, new MediaStateChangedEventArgs(Status, msg));
                    LogException(ex);
                }
            }
        }

        private MediaPlayer GetVideoPlayer()
        {
            var player = new MediaPlayer();
            player.MediaOpened += OnMediaPlayerOpened;
            player.CurrentStateChanged += OnMediaPlayerCurrentStateChangedAsync;
            player.MediaEnded += OnMediaPlayerEndedAsync;
            player.MediaFailed += OnMediaPlayerFailedAsync;
            return player;
        }

        private async void OnMediaPlayerFailedAsync(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            Pause();

            await _dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                // 在视频未加载时不对报错进行处理.
                if (Status == PlayerStatus.NotLoad)
                {
                    return;
                }

                if (args.ExtendedErrorCode?.HResult == -1072873851 || args.Error == MediaPlayerError.Unknown)
                {
                    // 不处理 Shutdown 造成的错误.
                    return;
                }

                var message = string.Empty;
                switch (args.Error)
                {
                    case MediaPlayerError.Aborted:
                        message = _resourceToolkit.GetLocaleString(LanguageNames.Aborted);
                        break;
                    case MediaPlayerError.NetworkError:
                        message = _resourceToolkit.GetLocaleString(LanguageNames.NetworkError);
                        break;
                    case MediaPlayerError.DecodingError:
                        message = _resourceToolkit.GetLocaleString(LanguageNames.DecodingError);
                        break;
                    case MediaPlayerError.SourceNotSupported:
                        message = _resourceToolkit.GetLocaleString(LanguageNames.SourceNotSupported);
                        break;
                    default:
                        break;
                }

                Status = PlayerStatus.Failed;
                var arg = new MediaStateChangedEventArgs(Status, args.ErrorMessage);
                StateChanged?.Invoke(this, arg);
                LogException(new Exception($"播放失败: {args.Error} | {args.ErrorMessage} | {args.ExtendedErrorCode}"));
            });
        }

        private async void OnMediaPlayerEndedAsync(MediaPlayer sender, object args)
        {
            await _dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                // 当视频播放中断时，尝试重连，但最多尝试2次
                if (_video != null && Position < Duration - TimeSpan.FromSeconds(1) && _videoRetryCount < 2)
                {
                    _videoRetryCount++;

                    // 此时认为应该尝试重新连接媒体源.
                    TryReconnectPlayer(_audioPlayer);
                    TryReconnectPlayer(_videoPlayer);
                    return;
                }

                Status = PlayerStatus.End;
                StateChanged?.Invoke(this, new MediaStateChangedEventArgs(Status, string.Empty));
            });
        }

        private async void OnMediaPlayerCurrentStateChangedAsync(MediaPlayer sender, object args)
        {
            await _dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    Status = sender.PlaybackSession.PlaybackState switch
                    {
                        MediaPlaybackState.Opening => PlayerStatus.Buffering,
                        MediaPlaybackState.Playing => PlayerStatus.Playing,
                        MediaPlaybackState.Buffering => PlayerStatus.Buffering,
                        MediaPlaybackState.Paused => PlayerStatus.Pause,
                        _ => PlayerStatus.NotLoad,
                    };
                }
                catch (Exception)
                {
                    Status = PlayerStatus.NotLoad;
                }

                StateChanged?.Invoke(this, new MediaStateChangedEventArgs(Status, string.Empty));
            });
        }

        private void OnMediaPlayerOpened(MediaPlayer sender, object args)
        {
            var session = sender.PlaybackSession;
            if (session != null)
            {
                session.PositionChanged -= OnPlayerPositionChangedAsync;

                if (_video != null)
                {
                    session.PositionChanged += OnPlayerPositionChangedAsync;
                }

                var autoPlay = _settingsToolkit.ReadLocalSetting(SettingNames.IsAutoPlayWhenLoaded, true);
                if (autoPlay)
                {
                    if (_mediaTimelineController != null)
                    {
                        _mediaTimelineController.Resume();
                    }
                    else
                    {
                        _videoPlayer.AutoPlay = true;
                        _videoPlayer.Play();
                    }
                }
            }

            MediaOpened?.Invoke(this, EventArgs.Empty);
        }

        private async void OnPlayerPositionChangedAsync(MediaPlaybackSession sender, object args)
        {
            await _dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                var duration = sender.NaturalDuration.TotalSeconds;
                var progress = sender.Position.TotalSeconds;
                if (progress >= duration)
                {
                    if (_mediaTimelineController != null)
                    {
                        _mediaTimelineController.Pause();
                    }
                    else if (_videoPlayer != null
                    && _videoPlayer.PlaybackSession != null
                    && _videoPlayer.PlaybackSession.CanPause
                    && _videoPlayer.TimelineController == null)
                    {
                        _videoPlayer.Pause();
                    }

                    if (progress == duration)
                    {
                        Status = PlayerStatus.End;
                        StateChanged?.Invoke(this, new MediaStateChangedEventArgs(Status, string.Empty));
                    }

                    return;
                }

                PositionChanged?.Invoke(this, new MediaPositionChangedEventArgs(sender.Position, sender.NaturalDuration));
            });
        }

        private VideoDecoderMode GetDecoderMode()
        {
            var decodeType = _settingsToolkit.ReadLocalSetting(SettingNames.DecodeType, DecodeType.Automatic);
            return decodeType switch
            {
                DecodeType.HardwareDecode => VideoDecoderMode.ForceSystemDecoder,
                DecodeType.SoftwareDecode => VideoDecoderMode.ForceFFmpegSoftwareDecoder,
                _ => VideoDecoderMode.Automatic
            };
        }

        private MediaTimelineController GetTimelineController()
        {
            var controller = new MediaTimelineController();
            return controller;
        }

        private void ClearMediaPlayerData(MediaPlayer mediaPlayer, MediaPlaybackItem playback, FFmpegMediaSource source, HttpRandomAccessStream stream)
        {
            if (mediaPlayer == null)
            {
                return;
            }

            mediaPlayer.MediaOpened -= OnMediaPlayerOpened;
            mediaPlayer.CurrentStateChanged -= OnMediaPlayerCurrentStateChangedAsync;
            mediaPlayer.MediaEnded -= OnMediaPlayerEndedAsync;
            mediaPlayer.MediaFailed -= OnMediaPlayerFailedAsync;

            if (mediaPlayer.PlaybackSession != null)
            {
                mediaPlayer.PlaybackSession.PositionChanged -= OnPlayerPositionChangedAsync;
            }

            playback?.Source?.Dispose();
            playback = null;

            source?.Dispose();
            source = null;

            mediaPlayer.Source = null;

            if (stream != null)
            {
                stream?.Dispose();
                stream = null;
            }
        }

        private void Clear()
        {
            if (_mediaTimelineController != null)
            {
                _mediaTimelineController.Pause();
                _mediaTimelineController.Position = TimeSpan.Zero;
                _mediaTimelineController = null;
            }

            ClearMediaPlayerData(_videoPlayer, _videoPlaybackItem, _videoFFSource, _videoStream);
            ClearMediaPlayerData(_audioPlayer, _audioPlaybackItem, _audioFFSource, _audioStream);

            Status = PlayerStatus.NotLoad;
        }

        private void TryReconnectPlayer(MediaPlayer player)
        {
            if (player.PlaybackSession != null && player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
            {
                // 该 Player 出了问题，重新播放一下.
                var position = Position;
                Play();
                _mediaTimelineController.Position = position;
            }
        }
    }
}