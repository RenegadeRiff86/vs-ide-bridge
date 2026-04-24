using System;
using System.Collections.Generic;

namespace VsIdeBridge.Diagnostics;

internal static class BestPracticeRuleMetadata
{
    internal sealed class RuleText(string guidance, string suggestedAction, string llmFixPrompt)
    {
        public string Guidance { get; } = guidance;

        public string SuggestedAction { get; } = suggestedAction;

        public string LlmFixPrompt { get; } = llmFixPrompt;
    }

    private static readonly Dictionary<string, RuleText> RuleTexts = CreateRuleTexts();

    public static RuleText? TryGetRuleText(string code)
    {
        return RuleTexts.TryGetValue(code, out RuleText? ruleText) ? ruleText : null;
    }

    private static Dictionary<string, RuleText> CreateRuleTexts()
    {
        return new Dictionary<string, RuleText>(StringComparer.OrdinalIgnoreCase)
        {
            [BestPracticeRuleCatalog.BP1001.Code] = new(
                "This repeated string likely carries shared meaning, so leaving copies inline makes updates brittle and invites drift.",
                "Extract the repeated string into a named constant or a shared readonly value with domain meaning.",
                "Replace the repeated string literal with a clearly named constant or shared readonly value. Keep the name domain-specific so future edits only need one change point."),
            [BestPracticeRuleCatalog.BP1002.Code] = new(
                "This repeated numeric literal probably encodes domain meaning, so readers cannot tell whether it is a business rule, indexing math, or an accidental duplicate.",
                "Use a named constant for domain values, or keep the literal only when it is obvious local arithmetic and add a short clarifying comment if needed.",
                "Rewrite this code so repeated numeric literals with domain meaning become named constants. If the value is only local arithmetic or indexing math, leave it inline and make that intent obvious."),
            [BestPracticeRuleCatalog.BP1003.Code] = new(
                "This floor-or-truncate cast hides rounding intent and often means the code really wanted integer division instead.",
                "Use integer division or an explicit rounding helper so the conversion behavior is obvious.",
                "Replace the suspicious floor-or-truncate cast with code that makes the rounding intent explicit. Prefer integer division when that is the real goal; otherwise use a clearly named rounding helper."),
            [BestPracticeRuleCatalog.BP1004.Code] = new(
                "This catch path suppresses exception intent, which makes failures invisible and encourages silent corruption or partial success.",
                "Handle the exception explicitly by writing it to the log with useful context, translating it, or rethrowing it.",
                "Fix this catch block so the exception is handled intentionally. If the block is empty or only contains comments, write the exception to the log with useful context, translate it to a narrower exception, or rethrow it. Do not leave a silent catch."),
            [BestPracticeRuleCatalog.BP1005.Code] = new(
                "Async void methods hide failures from callers and make cancellation, composition, and testing much harder.",
                "Return Task instead of async void unless this is a real event handler.",
                "Change this async void method to async Task unless it is a true event handler. Preserve behavior, but make exceptions observable to callers."),
            [BestPracticeRuleCatalog.BP1006.Code] = new(
                "Manual ownership with raw new and delete is error-prone because lifetime is easy to lose across branches and exceptions.",
                "Replace manual ownership with RAII and smart pointers.",
                "Refactor this allocation so ownership is expressed with RAII. Prefer std::unique_ptr or std::shared_ptr over raw new/delete unless the API contract truly requires raw ownership."),
            [BestPracticeRuleCatalog.BP1007.Code] = new(
                "A global using namespace in a header leaks names into every includer and makes collisions harder to diagnose.",
                "Remove the global using namespace from the header and qualify names instead.",
                "Remove the global using namespace from this header. Qualify names explicitly or introduce narrower aliases in implementation files instead."),
            [BestPracticeRuleCatalog.BP1008.Code] = new(
                "C-style casts hide the conversion kind, which makes unsafe or narrowing conversions harder to review.",
                "Replace the C-style cast with a named cast that shows the conversion intent.",
                "Replace the C-style cast with the narrowest named C++ cast that matches the intent, such as static_cast, reinterpret_cast, const_cast, or dynamic_cast."),
            [BestPracticeRuleCatalog.BP1009.Code] = new(
                "Exceptions that are swallowed or logged poorly leave production failures invisible and make postmortems harder.",
                "Log the exception details with the logger so failures stay observable.",
                "Update this exception handling path so the failure is observable. Log the exception with enough context to diagnose the operation that failed."),
            [BestPracticeRuleCatalog.BP1010.Code] = new(
                "A function without a clear contract forces callers to inspect implementation details before they can use it safely.",
                "Add a docstring and an explicit return contract so the function is easier to use.",
                "Add a short docstring that explains the function purpose, parameters, and return behavior. Make the return contract explicit if it is currently implicit."),
            [BestPracticeRuleCatalog.BP1011.Code] = new(
                "Imports in the middle of logic hide dependencies and can trigger order-sensitive behavior that is hard to spot.",
                "Move imports to the top of the file and group them consistently.",
                "Reorganize imports so they live at the top of the file in a consistent grouping order. Keep local imports only when there is a real lazy-import reason."),
            [BestPracticeRuleCatalog.BP1012.Code] = new(
                "This file is carrying too many responsibilities, which raises navigation cost and makes targeted changes riskier.",
                "Split the file into smaller focused types or helpers before adding more code.",
                "This file is too long. Identify cohesive groups of types or helpers that can stand alone, then use create_project to create a new class library and apply_diff to move those types out. Prioritize types with the fewest cross-file dependencies so each move is self-contained. Update any using directives and project references after each extraction."),
            [BestPracticeRuleCatalog.BP1013.Code] = new(
                "This method is long enough that control flow and state changes are hard to reason about in one pass.",
                "Extract smaller methods so each block has one clear job.",
                "Break this method into smaller helpers with names that explain each step. Preserve behavior, but reduce local branching and state juggling in the main method."),
            [BestPracticeRuleCatalog.BP1014.Code] = new(
                "This symbol name is too vague to communicate intent, so readers must infer purpose from surrounding code.",
                "Rename the symbol so its purpose is obvious without reading surrounding code.",
                "Rename this symbol to reflect its purpose, ownership, or role in the workflow. Avoid generic names that require nearby context to understand."),
            [BestPracticeRuleCatalog.BP1015.Code] = new(
                "Deep nesting hides the happy path and increases the chance of missed edge cases during edits.",
                "Flatten the control flow with guard clauses, extracted helpers, or simpler branching. Use file_outline first when you need a quick map of the surrounding type or method structure.",
                "Use file_outline first if you need a quick map of the surrounding type or method structure, then refactor this nested control flow so the main path is easier to read. Prefer guard clauses, early returns, or extracted helpers over additional nesting."),
            [BestPracticeRuleCatalog.BP1016.Code] = new(
                "Commented-out code becomes stale quickly and is a poor substitute for version control history.",
                "Delete the dead code and rely on version control instead of commented-out blocks.",
                "Remove the commented-out code block. Keep only comments that explain intent or constraints, and trust version control for old implementations."),
            [BestPracticeRuleCatalog.BP1017.Code] = new(
                "Inconsistent indentation makes structure harder to scan and increases diff noise.",
                "Normalize indentation to one style and reformat the file.",
                "Reformat this section so indentation is consistent with the surrounding file and project conventions. Do not change behavior while cleaning the layout."),
            [BestPracticeRuleCatalog.BP1018.Code] = new(
                "This type has accumulated too many responsibilities, so changes in one area are likely to surprise other areas.",
                "Extract one concrete responsibility at a time into focused helpers, services, or state objects before extending this type further.",
                "Refactor this large type by identifying one overloaded responsibility and extracting it into a focused helper, service, or state object. Preserve public behavior, keep the API stable where possible, and make the remaining type clearly narrower in scope."),
            [BestPracticeRuleCatalog.BP1019.Code] = new(
                "Disposable resources that are not scoped explicitly are easy to leak across exceptions and early returns.",
                "Wrap the disposable resource in using or await using so cleanup is guaranteed.",
                "Refactor this resource usage so disposal is explicit and exception-safe. Prefer using or await using around the narrowest lifetime that still works."),
            [BestPracticeRuleCatalog.BP1020.Code] = new(
                "Reading the current time repeatedly inside a loop can create inconsistent comparisons and makes tests harder to stabilize.",
                "Capture the time value once before the loop and reuse it inside the loop body.",
                "Move the time capture outside the loop unless each iteration truly needs a fresh timestamp. Reuse one value so comparisons and tests stay stable."),
            [BestPracticeRuleCatalog.BP1021.Code] = new(
                "Dynamic or object-based contracts hide type intent and force callers to discover expected shapes at runtime.",
                "Replace dynamic or object parameters with specific types or generics.",
                "Tighten this API contract so callers and implementers see concrete types. Prefer explicit models or generics over object or dynamic when possible."),
            [BestPracticeRuleCatalog.BP1022.Code] = new(
                "Raw allocation leaves ownership ambiguous, which increases leak and double-delete risk.",
                "Use std::make_unique or std::make_shared instead of raw new.",
                "Replace raw heap allocation with std::make_unique or std::make_shared when ownership is shared or singular. Keep raw pointers only as non-owning views."),
            [BestPracticeRuleCatalog.BP1023.Code] = new(
                "Macros obscure type safety, scope, and debugger behavior compared with language features.",
                "Replace macros with constexpr values, inline functions, or templates.",
                "Refactor this macro usage into a safer language feature such as constexpr, an inline function, or a template. Preserve behavior while making intent explicit to the compiler."),
            [BestPracticeRuleCatalog.BP1024.Code] = new(
                "Inheritance depth and hierarchy complexity make state and override behavior harder to predict.",
                "Prefer composition over deep or wide inheritance.",
                "Reduce this inheritance complexity by extracting composable helpers or owned collaborators instead of adding more hierarchy depth."),
            [BestPracticeRuleCatalog.BP1025.Code] = new(
                "Copying large values unnecessarily adds avoidable work and can hide whether mutation is intended.",
                "Pass large values by const reference when you do not need a copy.",
                "Change this API or call site to pass large values by const reference unless you truly need ownership or mutation through a copy."),
            [BestPracticeRuleCatalog.BP1026.Code] = new(
                "Comparing directly to True or False adds noise and can hide the simpler boolean intent.",
                "Use truthiness directly instead of comparing to True or False.",
                "Simplify this boolean expression to use direct truthiness or falsiness rather than explicit comparisons to True or False."),
            [BestPracticeRuleCatalog.BP1027.Code] = new(
                "This type is acting like a shared property bag, which spreads state everywhere without a strong behavioral boundary.",
                "Move the shared state behind a focused service or model instead of growing an accessor-only class.",
                "Refactor this property-heavy type so state and behavior are grouped into focused models or services. Avoid adding more passive accessors to the same bag of state."),
            [BestPracticeRuleCatalog.BP1028.Code] = new(
                "Comments that only restate obvious code add reading cost without preserving real intent.",
                "Delete comments that only restate obvious code and keep the ones that add real intent.",
                "Remove low-value comments that merely narrate the code. Keep or add comments only where they explain intent, constraints, or non-obvious decisions."),
            [BestPracticeRuleCatalog.BP1029.Code] = new(
                "Namespace and folder drift makes ownership harder to infer and complicates navigation.",
                "Align folders with namespaces and use owning-type folders instead of dotted partial filenames.",
                "Reshape this file or namespace placement so the folder path matches the logical ownership. Prefer clear owner folders over dotted pseudo-namespaces in filenames."),
            [BestPracticeRuleCatalog.BP1030.Code] = new(
                "Encoding drift can corrupt text or produce noisy diffs that hide the real code change.",
                "Preserve UTF-8 text exactly and reload the file if an external edit changed the encoding.",
                "Fix this text handling so UTF-8 content is preserved exactly. Avoid introducing encoding churn, and reload the document if an external edit changed the encoding unexpectedly."),
            [BestPracticeRuleCatalog.BP1031.Code] = new(
                "Write-Host is presentation-only and makes automation output harder to capture, test, or redirect.",
                "Use Write-Output, Write-Information, or structured logging instead of Write-Host in automation scripts.",
                "Replace Write-Host with an output or logging mechanism that automation can capture, redirect, or test. Choose the stream that matches the script intent."),
            [BestPracticeRuleCatalog.BP1032.Code] = new(
                "Without strict mode, PowerShell silently accepts misspellings and uninitialized values that should fail fast.",
                "Enable Set-StrictMode -Version Latest near the top of the script so mistakes fail fast.",
                "Add Set-StrictMode -Version Latest near the top of the script unless there is a documented compatibility reason not to. Keep the script behavior explicit under strict mode."),
            [BestPracticeRuleCatalog.BP1033.Code] = new(
                "Implicit typing hides the concrete type at the declaration site, which makes code review slower when the right type is not obvious.",
                "Prefer the explicit type unless the concrete type would be excessively noisy.",
                "Replace this var declaration with the explicit concrete type unless the type name is unreasonably noisy or the right-hand side already makes the type unmistakable."),
            [BestPracticeRuleCatalog.BP1034.Code] = new(
                "A broad catch usually hides the real failure contract and encourages accidental swallowing of unrelated problems.",
                "Catch the narrowest exception type that matches the code you expect to fail.",
                "Refactor this catch so it handles the narrowest expected exception type. Preserve any logging or cleanup, but do not leave a broad catch unless this is a top-level exception boundary."),
            [BestPracticeRuleCatalog.BP1035.Code] = new(
                "Framework type aliases like System.String add ceremony without improving clarity in normal C# code.",
                "Prefer C# keyword aliases such as string, int, and bool in ordinary code.",
                "Replace framework type aliases like System.String with normal C# keyword aliases such as string, int, and bool unless the full type name is required for disambiguation."),
            [BestPracticeRuleCatalog.BP1036.Code] = new(
                "Without Option Strict On, VB allows late-bound and narrowing behavior that often hides correctness issues until runtime.",
                "Enable Option Strict On and remove code that depends on relaxed conversions.",
                "Turn Option Strict On for this file or project and adjust the code so conversions and late binding are explicit rather than implicit."),
            [BestPracticeRuleCatalog.BP1037.Code] = new(
                "Putting multiple VB statements on one line makes control flow and diff review harder to follow.",
                "Split multiple statements onto separate lines.",
                "Rewrite this VB code so each statement gets its own line. Keep the same behavior, but make sequencing explicit."),
            [BestPracticeRuleCatalog.BP1038.Code] = new(
                "Explicit line continuation in VB is usually unnecessary and adds visual clutter when the compiler can infer it.",
                "Remove explicit line continuation where VB can infer the continuation safely.",
                "Simplify this VB statement by removing explicit continuation markers where the language already supports implicit continuation."),
            [BestPracticeRuleCatalog.BP1039.Code] = new(
                "Mutation in F# should stand out because pervasive mutable state undermines the language’s strongest readability and correctness advantages.",
                "Prefer immutable values and isolate mutation to the smallest boundary that truly needs it.",
                "Refactor this F# code so mutable state is minimized or isolated. Prefer immutable values and return new state rather than mutating shared values when practical."),
            [BestPracticeRuleCatalog.BP1040.Code] = new(
                "Block comments in F# are easier to let drift than small line comments placed next to the exact code they explain.",
                "Prefer concise line comments and keep comments close to the code they justify.",
                "Replace or trim this F# block comment so comments stay short, local, and intent-focused. Keep only explanation that the code itself cannot express."),
            [BestPracticeRuleCatalog.BP1041.Code] = new(
                "Comparing to None with == or != is less explicit than Python’s identity operators and can be surprising with overloaded equality.",
                "Use 'is None' or 'is not None' for None checks.",
                "Rewrite this Python None comparison to use 'is None' or 'is not None' instead of == or != so the intent is identity comparison."),
            [BestPracticeRuleCatalog.BP1042.Code] = new(
                "PowerShell aliases make scripts harder to read and less portable across readers who do not know the shorthand.",
                "Replace cmdlet aliases with full cmdlet names.",
                "Replace this PowerShell alias with the full cmdlet name so the script is easier to read, search, and maintain."),
            [BestPracticeRuleCatalog.BP1043.Code] = new(
                "Switching to the Visual Studio UI thread too early and then doing substantial work there can freeze the IDE and make the bridge look hung.",
                "Keep the command on a background thread by default and limit UI-thread work to the smallest block that truly requires DTE or shell services.",
                "Refactor this method so it stays off the Visual Studio UI thread by default. Move SwitchToMainThreadAsync as close as possible to the specific DTE or shell call that needs it, and move back to background work for parsing, shaping, and serialization."),
            [BestPracticeRuleCatalog.BP1044.Code] = new(
                "Diagnostic suppression hides warnings and messages from both humans and models, which makes the codebase look healthier than it really is.",
                "Remove pragma, .editorconfig, NoWarn, ruleset, or SuppressMessage suppression settings and fix the underlying diagnostic unless there is a rare, documented compatibility reason not to.",
                "Delete this diagnostic suppression and fix the underlying analyzer or compiler diagnostic directly. This includes pragma suppression, .editorconfig severity downgrades to none or silent, NoWarn entries, ruleset suppressions, and SuppressMessage attributes. If the current diagnostics result shows more than 10 BP1044 rows, stop and ask the user before making a broad suppression cleanup pass. Only keep a suppression when there is a documented, unavoidable compatibility reason, and then explain that reason next to the suppression."),
            [BestPracticeRuleCatalog.BP1045.Code] = new(
                "Marker comments like TODO, FIXME, XXX, HACK, TBD, and BUGBUG are easy to forget in code review and leave uncertain work hidden in the codebase instead of tracked in a visible backlog.",
                "Resolve the marker comment or move the work into a tracked issue, then remove the marker from the code.",
                "Find the work described by this marker comment and either implement it now or move it into a tracked issue or work item with enough context for follow-up. Remove the marker comment from the code once the work is tracked or resolved. Treat TODO, FIXME, XXX, HACK, TBD, and BUGBUG as reminders to finish or formally track the work, not as permanent code annotations."),
        };
    }
}
