using System.Linq;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.Models.TranscodeTestDtos;
using Xunit;

namespace Jellyfin.Api.Tests.Helpers
{
    public class TranscodeTestStressControllerTests
    {
        private const double Threshold = 21.6;

        private static TranscodeTestStressController Create(int maxStreams = 8)
            => new(Threshold, rampSeconds: 12, failSeconds: 5, startTimeoutSeconds: 30, maxStreams, now: 0);

        private static TranscodeTestStreamReading[] Streams(params double?[] fps)
            => fps.Select((f, i) => new TranscodeTestStreamReading(i + 1, f, 20)).ToArray();

        [Fact]
        public void Tick_NewStreamNotMeasuredYet_WarmsUp()
        {
            var controller = Create();

            var decision = controller.Tick(1, Streams((double?)null));

            Assert.Equal(TranscodeTestStressAction.Wait, decision.Action);
            Assert.Equal(TranscodeTestStressPhase.WarmingUp, decision.Phase);
            Assert.Equal(12, decision.NextStreamAt);
        }

        [Fact]
        public void Tick_AllKeepUp_AddsAStreamAfterTheInterval()
        {
            var controller = Create();

            Assert.Equal(TranscodeTestStressPhase.KeepingUp, controller.Tick(6, Streams(30)).Phase);
            Assert.Equal(TranscodeTestStressAction.Wait, controller.Tick(11.5, Streams(30)).Action);
            var decision = controller.Tick(12, Streams(30));

            Assert.Equal(TranscodeTestStressAction.AddStream, decision.Action);
            Assert.Equal(1, controller.MaxStable);
        }

        [Fact]
        public void Tick_OneStreamBelow_FallsBehindAtOnce()
        {
            var controller = Create();

            var decision = controller.Tick(3, Streams(30, 20));

            Assert.Equal(TranscodeTestStressPhase.FallingBehind, decision.Phase);
            Assert.Equal(2, decision.StreamId);
            Assert.Equal(8, decision.EndsAt);
        }

        [Fact]
        public void Tick_NewStreamIsExcused_OthersAreStillJudged()
        {
            var controller = Create();

            var decision = controller.Tick(3, Streams(20, null));

            Assert.Equal(TranscodeTestStressPhase.FallingBehind, decision.Phase);
            Assert.Equal(1, decision.StreamId);
        }

        [Fact]
        public void Tick_BelowForTheFailTime_ReachesTheLimit()
        {
            var controller = Create();
            controller.Tick(12, Streams(30));
            controller.StreamAdded(12);

            controller.Tick(13, Streams(25, 20));
            Assert.Equal(TranscodeTestStressAction.Wait, controller.Tick(17.5, Streams(25, 20)).Action);
            var decision = controller.Tick(18, Streams(25, 20));

            Assert.Equal(TranscodeTestStressAction.LimitReached, decision.Action);
            Assert.Equal(2, decision.StreamId);
            Assert.Equal(20, decision.Fps);
            Assert.Equal(1, controller.MaxStable);
        }

        [Fact]
        public void Tick_RecoveringStream_RestartsTheInterval()
        {
            var controller = Create();
            controller.Tick(3, Streams(20));

            var recovered = controller.Tick(6, Streams(25));
            Assert.Equal(TranscodeTestStressPhase.KeepingUp, recovered.Phase);
            Assert.Equal(15, recovered.NextStreamAt);

            Assert.Equal(TranscodeTestStressAction.Wait, controller.Tick(14, Streams(25)).Action);
            Assert.Equal(TranscodeTestStressAction.AddStream, controller.Tick(15, Streams(25)).Action);
        }

        [Fact]
        public void Tick_Dip_IsReportedOncePerDip()
        {
            var controller = Create();

            var first = controller.Tick(3, Streams(30, 20));
            Assert.True(first.DipStarted);
            Assert.Equal(2, first.StreamId);
            Assert.Equal(20, first.Fps);

            // Still below, or another stream joining the dip: the same dip.
            Assert.False(controller.Tick(4, Streams(30, 20)).DipStarted);
            Assert.False(controller.Tick(5, Streams(20, 20)).DipStarted);

            // All keep up again, then a new dip.
            Assert.False(controller.Tick(6, Streams(30, 30)).DipStarted);
            Assert.True(controller.Tick(7, Streams(19, 30)).DipStarted);
        }

        [Fact]
        public void Tick_BriefDips_NeverAddUpToAFailure()
        {
            var controller = Create();

            for (var t = 1.0; t < 30; t += 2)
            {
                var decision = controller.Tick(t, Streams(20));
                Assert.NotEqual(TranscodeTestStressAction.LimitReached, decision.Action);
                controller.Tick(t + 1, Streams(25));
            }
        }

        [Fact]
        public void Tick_RestartedStream_StartsItsTimeBelowAgain()
        {
            var controller = Create();
            for (var t = 30.0; t <= 33; t++)
            {
                Assert.NotEqual(TranscodeTestStressAction.LimitReached, controller.Tick(t, Streams(20)).Action);
            }

            // The file ended at 33 s; the new process is not measured at 34 and 35 s.
            controller.StreamRestarted(1);
            controller.Tick(34, Streams((double?)null));
            controller.Tick(35, Streams((double?)null));
            var below = controller.Tick(36, Streams(20));

            Assert.Equal(TranscodeTestStressAction.Wait, below.Action);
            Assert.Equal(TranscodeTestStressPhase.FallingBehind, below.Phase);
            Assert.True(below.DipStarted);
            Assert.Equal(41, below.EndsAt);
            Assert.Equal(TranscodeTestStressAction.Wait, controller.Tick(40.5, Streams(20)).Action);
            Assert.Equal(TranscodeTestStressAction.LimitReached, controller.Tick(41, Streams(20)).Action);
        }

        [Fact]
        public void Tick_RestartedStreamThatKeepsUp_EndsTheDip()
        {
            var controller = Create();
            controller.Tick(30, Streams(20));
            controller.Tick(33, Streams(20));

            controller.StreamRestarted(1);
            var unmeasured = controller.Tick(34, Streams((double?)null));
            Assert.Equal(TranscodeTestStressPhase.KeepingUp, unmeasured.Phase);
            Assert.Equal(TranscodeTestStressPhase.KeepingUp, controller.Tick(35, Streams((double?)null)).Phase);
            var keptUp = controller.Tick(36, Streams(27));

            Assert.Equal(TranscodeTestStressPhase.KeepingUp, keptUp.Phase);
            Assert.False(keptUp.DipStarted);
        }

        [Fact]
        public void Tick_NullReadingAfterRestart_DoesNotExtendADip()
        {
            var controller = Create();
            controller.Tick(3, Streams(30, 20));

            controller.StreamRestarted(2);
            var restarted = controller.Tick(4, Streams(30, null));

            Assert.NotEqual(TranscodeTestStressPhase.FallingBehind, restarted.Phase);
            Assert.Null(restarted.EndsAt);

            // Below again later: a new dip with its own fail time.
            var again = controller.Tick(9, Streams(30, 20));
            Assert.True(again.DipStarted);
            Assert.Equal(14, again.EndsAt);
        }

        [Fact]
        public void Tick_RestartedStreamNotMeasuredYet_DoesNotHoldBackTheNextStream()
        {
            var controller = Create();
            controller.Tick(6, Streams(30, 30));

            controller.StreamRestarted(2);

            Assert.Equal(TranscodeTestStressAction.AddStream, controller.Tick(12, Streams(30, null)).Action);
        }

        [Fact]
        public void Tick_RestartedStreamOnceMeasured_IsJudgedAsBefore()
        {
            var controller = Create();
            controller.StreamRestarted(1);
            controller.Tick(2, Streams((double?)null));
            controller.Tick(3, Streams(20));

            Assert.Equal(TranscodeTestStressAction.LimitReached, controller.Tick(8, Streams(20)).Action);
        }

        [Fact]
        public void Tick_StreamLimitWhileAllKeepUp_StopsAtMaxStreams()
        {
            var controller = Create(maxStreams: 2);

            var decision = controller.Tick(12, Streams(30, 30));

            Assert.Equal(TranscodeTestStressAction.MaxStreams, decision.Action);
            Assert.Equal(2, controller.MaxStable);
        }

        [Fact]
        public void Tick_NoFramesForTooLong_Stalls()
        {
            var controller = Create();

            var decision = controller.Tick(40, [new TranscodeTestStreamReading(1, 30, 40), new TranscodeTestStreamReading(2, null, 31)]);

            Assert.Equal(TranscodeTestStressAction.Stalled, decision.Action);
            Assert.Equal(2, decision.StreamId);
        }
    }
}
