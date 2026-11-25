using NSubstitute;
using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Api.Features.Video.TacticalAi;
using PoRedoMedia.Client.LocalAi;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Unit;

public sealed class TacticalAiTests
{
    [Fact]
    public void UtilityCueScorer_VetoesNarratorOverlapAndRewardsHighEnergySpacing()
    {
        var vetoed = UtilityCueScorer.Score(new(0.9f, 0.9f, 5f, 0.9f, 1f, OverlapsNarrator: true));
        var strong = UtilityCueScorer.Score(new(0.9f, 0.9f, 4f, 0.8f, 1f, OverlapsNarrator: false));
        var weak = UtilityCueScorer.Score(new(0.1f, 0.2f, 0.2f, 0.1f, 0.2f, OverlapsNarrator: false));

        Assert.Equal(0f, vetoed);
        Assert.True(strong > 0.7f);
        Assert.True(strong > weak);
    }

    [Fact]
    public void GoapComedyPlanner_PlansFeintedPayoffAndCallbackArc()
    {
        var goal = GoapComedyPlanner.BeatState.HasSetup
            | GoapComedyPlanner.BeatState.HasPayoff
            | GoapComedyPlanner.BeatState.HasCallback;

        var plan = GoapComedyPlanner.Plan(GoapComedyPlanner.BeatState.None, goal);

        Assert.NotEmpty(plan);
        Assert.Equal("EstablishSetup", plan[0].Name);
        Assert.Contains(plan, a => a.Name == "CallbackStinger");
    }

    [Fact]
    public void MctsTurnOptimizer_AvoidsNarratorSpeechWindowsAndMaximizesUtility()
    {
        var cues = new MctsTurnOptimizer.CueCandidate[]
        {
            new(1000, 600, 0.9f, IsVoice: false),
            new(1100, 700, 0.95f, IsVoice: true),
        };
        var speech = new MctsTurnOptimizer.SpeechWindow[] { new(900, 1500) };

        var plan = MctsTurnOptimizer.Optimize(cues, speech, videoDurationMs: 10_000, rollouts: 64);

        Assert.Equal(2, plan.Count);
        foreach (var item in plan.Where(p => !p.Dropped))
            Assert.True(item.ScheduledMs >= 1700);
    }

    [Fact]
    public void SpatialInfluenceMap_SpreadsMultipleStickersAwayFromCaptionBands()
    {
        var map = new SpatialInfluenceMap();
        map.ApplyVisionBoxes([new VisionBoxDto("face", 0.4, 0.3, 0.2, 0.25)], flankSubject: true);

        var first = map.PickAndReserve();
        var second = map.PickAndReserve();

        Assert.InRange(first.Y, 0.12, 0.88);
        Assert.True(Math.Abs(first.X - second.X) > 0.1 || Math.Abs(first.Y - second.Y) > 0.1);
    }

    [Fact]
    public void ComedyBlackboard_InheritsParentAndDecaysSalience()
    {
        var series = new ComedyBlackboard();
        series.Set("persona", "brainrot", salience: 0.9f, ttl: 5);
        var clip = new ComedyBlackboard(series);
        clip.Set("gag", "leftover pad thai", salience: 1.0f, ttl: 2);

        Assert.Equal("brainrot", clip.Get("persona"));
        Assert.Equal(2, clip.TopCallbacks().Count);

        clip.Decay();
        clip.Decay();
        Assert.Null(clip.Get("gag"));
        Assert.Equal("brainrot", clip.Get("persona"));
    }

    [Fact]
    public void DynamicPacingDirector_TransitionsToSilenceFeintAfterSustainedPeak()
    {
        var dda = new DynamicPacingDirector();
        for (var i = 0; i < 6; i++)
            dda.ObserveAndModulate(0.9f);

        Assert.True(dda.CurrentPhase is DynamicPacingDirector.Phase.SilenceFeint or DynamicPacingDirector.Phase.Relax);
    }

    [Fact]
    public void SilenceFeintDetector_DetectsSuddenDropAfterLoudHit()
    {
        var rms = new float[60];
        for (var i = 0; i < 20; i++) rms[i] = 0.6f;
        for (var i = 20; i < 60; i++) rms[i] = 0.01f;

        var feints = SilenceFeintDetector.Detect(rms, framesPerSecond: 10, minDropDb: 12f);

        Assert.NotEmpty(feints);
        Assert.InRange(feints[0].TimestampMs, 1500, 2600);
    }

    [Fact]
    public void Ucb1SoundBandit_ExploresAlternativeAfterRepeatedPicks()
    {
        var bandit = new Ucb1SoundBandit(explorationC: 0.4f);
        var candidates = new (string, float)[] { ("vine-boom", 0.85f), ("bruh", 0.82f) };

        var first = bandit.Select(candidates);
        var second = bandit.Select(candidates);

        Assert.Equal("vine-boom", first);
        Assert.Equal("bruh", second);
    }

    [Fact]
    public void MarkovAndBehavioralModels_PredictTransitionsAndTuneMix()
    {
        var markov = new MarkovTastePredictor();
        markov.Observe("MemeCaption");
        markov.Observe("RapRoast");
        markov.Observe("Memeify");
        markov.Observe("MemeCaption");
        markov.Observe("RapRoast");

        var (next, confidence) = markov.PredictNext();
        Assert.Equal("Memeify", next);
        Assert.True(confidence >= 0.9f);

        var taste = new BehavioralTasteModel();
        var wild = MemeMix.Default.WithRoast(3).WithMemes(3) with { Chaos = 3 };
        for (var i = 0; i < 5; i++)
            taste.RecordFeedback(wild, reward: 1.0f);

        var rec = taste.Recommend(MemeMix.Default);
        Assert.True(rec.RoastLevel >= 2 && rec.MemeLevel >= 2);
    }

    [Fact]
    public async Task AsyncAiOrchestrator_HandlesTournamentCriticPlaybookAndHybridIndex()
    {
        // #11 Speculative multi-draft tournament
        var winner = await AsyncAiOrchestrator.RunDraftTournamentAsync(
            (i, _) => Task.FromResult($"Draft {i}"),
            (drafts, _) => Task.FromResult(drafts.Select((_, i) => i == 1 ? 9.5 : 5.0).ToArray()),
            draftCount: 3);
        Assert.Equal("Draft 1", winner);

        // #16 Adversarial self-play RoastCritic loop
        var critic = Substitute.For<IRoastCritic>();
        critic.CritiqueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RoastCritique(5.0, "Sharper punchline"));
        var (refined, score, rewritten) = await AsyncAiOrchestrator.RefineWithCriticAsync(
            "roast", "weak draft", critic, (_, _, _) => Task.FromResult("sharp rewrite"));
        Assert.True(rewritten);
        Assert.Equal("sharp rewrite", refined);
        Assert.Equal(5.0, score);

        // #17 Hybrid-cached semantic sound index
        var index = new HybridSoundIndex();
        index.Index("vine-boom", ["impact", "boom", "sudden", "shock"]);
        index.Index("crickets", ["silence", "awkward", "deadpan"]);
        var hits = index.Search("awkward deadpan silence");
        Assert.NotEmpty(hits);
        Assert.Equal("crickets", hits[0].SoundId);

        // #19 Adaptive series comedy playbook
        var bb = new ComedyBlackboard();
        bb.Set("persona", "sitcom");
        bb.Set("callback", "missing pad thai");
        var playbook = AsyncAiOrchestrator.BuildPlaybook("office-series", bb);
        Assert.Equal("sitcom", playbook.Persona);
        Assert.NotEmpty(playbook.BeatArc);
        Assert.Contains("callback: missing pad thai", playbook.CallbackMotifs);
    }
}
