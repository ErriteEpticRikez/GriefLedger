using GriefLedger.Rollback;
using Xunit;

namespace GriefLedger.ReflectionTests;

public sealed class ExactRollbackCommandTests {
    [Fact]
    public void Parser_prefers_immutable_uid_and_keeps_the_default_radius() {
        bool parsed = ExactRollbackCommandParser.TryParse(["-u", "immutable-target"], out var options, out var error);

        Assert.True(parsed, error);
        Assert.NotNull(options);
        Assert.Equal("immutable-target", options.PlayerUid);
        Assert.Null(options.PlayerName);
        Assert.Equal(Commands.DefaultExactRollbackRadius, options.Radius);
        Assert.Null(options.BeforeSourceIdExclusive);
    }

    [Theory]
    [InlineData("-p", "Ari", "-u", "uid-ari")]
    [InlineData("-u", "uid-ari", "-r", "257")]
    [InlineData("-p", "Ari", "-r", "-1")]
    [InlineData("-u", "uid-ari", "-b", "0")]
    [InlineData("-u", "uid-ari", "-b", "9", "-b", "8")]
    [InlineData("-q", "value")]
    public void Parser_rejects_ambiguous_or_out_of_bounds_input(params string[] words) {
        bool parsed = ExactRollbackCommandParser.TryParse(words, out var options, out var error);

        Assert.False(parsed);
        Assert.Null(options);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Parser_accepts_the_positive_cursor_from_a_prior_page() {
        bool parsed = ExactRollbackCommandParser.TryParse(
            ["-u", "immutable-target", "-b", "9223372036854775807"], out var options, out var error);

        Assert.True(parsed, error);
        Assert.NotNull(options);
        Assert.Equal(long.MaxValue, options.BeforeSourceIdExclusive);
    }

    [Fact]
    public void Parser_accepts_name_only_when_it_is_the_single_requested_identity() {
        bool parsed = ExactRollbackCommandParser.TryParse(["-p", "Ari", "-r", "256"], out var options, out var error);

        Assert.True(parsed, error);
        Assert.NotNull(options);
        Assert.Null(options.PlayerUid);
        Assert.Equal("Ari", options.PlayerName);
        Assert.Equal(BlockRollbackLimits.MaximumRadius, options.Radius);
    }

    [Fact]
    public void Result_format_reports_boundaries_outcomes_and_stable_reason_counts() {
        var result = new BlockRollbackResult(41, 53, null, [
            new BlockRollbackAttemptResult(1, BlockMutationRollbackOutcome.Succeeded, null, 54),
            new BlockRollbackAttemptResult(2, BlockMutationRollbackOutcome.Failed, "current-state-mismatch", 55),
            new BlockRollbackAttemptResult(3, BlockMutationRollbackOutcome.Skipped, "later-other-player", 56),
            new BlockRollbackAttemptResult(4, BlockMutationRollbackOutcome.Skipped, "later-other-player", 57)
        ]);

        Assert.Equal(
            "cutoff #41; history through #53; selected=4, processed=4, unprocessed=0; succeeded=1, failed=1, skipped=2; has-more-candidates=false; reasons: current-state-mismatch=1, later-other-player=2, succeeded=1.",
            Commands.FormatExactRollbackResult(result));
        Assert.False(Commands.IsCleanExactRollbackCompletion(result));
    }

    [Fact]
    public void Result_format_reports_a_bounded_page_continuation_cursor() {
        var result = new BlockRollbackResult(41, 53, null, [
            new BlockRollbackAttemptResult(9, BlockMutationRollbackOutcome.Succeeded, null, 54)
        ], totalSelectedSourceCount: 1, hasMoreCandidates: true, continuationBeforeSourceId: 9);

        Assert.Contains("has-more-candidates=true, continuation-before=#9",
            Commands.FormatExactRollbackResult(result));
        Assert.False(Commands.IsCleanExactRollbackCompletion(result));
    }

    [Fact]
    public void Only_a_fully_successful_final_page_is_a_clean_completion() {
        var result = new BlockRollbackResult(41, 53, null, [
            new BlockRollbackAttemptResult(9, BlockMutationRollbackOutcome.Succeeded, null, 54)
        ]);

        Assert.True(Commands.IsCleanExactRollbackCompletion(result));
    }

    [Fact]
    public void Stopped_result_keeps_selected_and_unprocessed_counts_visible() {
        var result = new BlockRollbackResult(41, 53, BlockRollbackFailureCodes.BatchStopped, [
            new BlockRollbackAttemptResult(9, BlockMutationRollbackOutcome.Failed,
                BlockRollbackFailureCodes.RestoreFailed, 54)
        ], totalSelectedSourceCount: 3);

        Assert.Equal(3, result.TotalSelectedSourceCount);
        Assert.Single(result.Attempts);
        Assert.Equal(2, result.UnprocessedSourceCount);
        Assert.Contains("selected=3, processed=1, unprocessed=2", Commands.FormatExactRollbackResult(result));
    }

    [Fact]
    public void Queued_message_callback_catches_a_stale_player_send_failure() {
        var expected = new InvalidOperationException("stale player");
        Exception? observed = null;

        Commands.InvokeMessageCallbackSafely(
            () => throw expected,
            exception => observed = exception
        );

        Assert.Same(expected, observed);
    }

    [Fact]
    public void Console_caller_without_an_entity_has_no_invented_rollback_center() {
        Assert.False(Commands.TryGetExactRollbackCenter(null, out var center));
        Assert.Null(center);
    }
}
