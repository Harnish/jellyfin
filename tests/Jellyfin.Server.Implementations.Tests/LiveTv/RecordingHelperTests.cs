using System;
using Emby.Server.Implementations.LiveTv.EmbyTV;
using MediaBrowser.Controller.LiveTv;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.LiveTv
{
    public static class RecordingHelperTests
    {
        public static TheoryData<string, TimerInfo> GetRecordingName_Success_TestData()
        {
            var data = new TheoryData<string, TimerInfo>();

            data.Add(
                "The Incredibles 2020_04_20_21_06_00",
                new TimerInfo
                {
                    Name = "The Incredibles",
                    StartDate = new DateTime(2020, 4, 20, 21, 6, 0, DateTimeKind.Local),
                    IsMovie = true
                });

            data.Add(
                "The Incredibles (2004)",
                new TimerInfo
                {
                    Name = "The Incredibles",
                    IsMovie = true,
                    ProductionYear = 2004
                });
            data.Add(
                "The Big Bang Theory 2020_04_20_21_06_00",
                new TimerInfo
                {
                    Name = "The Big Bang Theory",
                    StartDate = new DateTime(2020, 4, 20, 21, 6, 0, DateTimeKind.Local),
                    IsProgramSeries = true,
                });
            data.Add(
                "The Big Bang Theory S12E10",
                new TimerInfo
                {
                    Name = "The Big Bang Theory",
                    IsProgramSeries = true,
                    SeasonNumber = 12,
                    EpisodeNumber = 10
                });
            data.Add(
                "The Big Bang Theory S12E10 The VCR Illumination",
                new TimerInfo
                {
                    Name = "The Big Bang Theory",
                    IsProgramSeries = true,
                    SeasonNumber = 12,
                    EpisodeNumber = 10,
                    EpisodeTitle = "The VCR Illumination"
                });
            data.Add(
                "The Big Bang Theory 2018-12-06",
                new TimerInfo
                {
                    Name = "The Big Bang Theory",
                    IsProgramSeries = true,
                    OriginalAirDate = new DateTime(2018, 12, 6)
                });

            data.Add(
                "The Big Bang Theory 2018-12-06 - The VCR Illumination",
                new TimerInfo
                {
                    Name = "The Big Bang Theory",
                    IsProgramSeries = true,
                    OriginalAirDate = new DateTime(2018, 12, 6),
                    EpisodeTitle = "The VCR Illumination"
                });

            data.Add(
                "The Big Bang Theory 2018_12_06_21_06_00 - The VCR Illumination",
                new TimerInfo
                {
                    Name = "The Big Bang Theory",
                    StartDate = new DateTime(2018, 12, 6, 21, 6, 0, DateTimeKind.Local),
                    IsProgramSeries = true,
                    OriginalAirDate = new DateTime(2018, 12, 6),
                    EpisodeTitle = "The VCR Illumination"
                });

            return data;
        }

        [Theory]
        [MemberData(nameof(GetRecordingName_Success_TestData))]
        public static void GetRecordingName_Success(string expected, TimerInfo timerInfo)
        {
            Assert.Equal(expected, RecordingHelper.GetRecordingName(timerInfo));
        }
    }
}
