namespace PitchGenApi.Services
{
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using PitchGenApi.Interfaces;
    using PitchGenApi.Model;
    using PitchGenApi.Model.DTOs;

    public sealed record AiEmailSearchOutcome(
        PitchResult SearchResult,
        string ModelName,
        string FinalPrompt,
        JArray Results,
        AiCompanyDetails? Company);

    /// <summary>
    /// The employer facts the email instruction asks for alongside the
    /// addresses. Each is null when the model could not source it.
    /// </summary>
    public sealed record AiCompanyDetails(
        string? Website,
        string? Industry,
        string? Size);

    /// <summary>
    /// The four-stage email unlock: the 30-day cache, then Prospeo, then an AI
    /// web search, then Hunter behind it.
    ///
    /// This used to live in ExtensionController, which made it reachable only
    /// over HTTP by a signed-in extension. It is a service now because the
    /// Audience Assurance email check runs the same four stages over a whole
    /// list, in a background job that has no HttpContext to read a claim from
    /// and no controller to call. Nothing about the stages changed in the move.
    ///
    /// Credit is deducted per contact, inside CompleteProspeoUnlockAsync and
    /// only once an address has actually been produced. A run that finds
    /// nothing costs nothing.
    /// </summary>
    public class EmailUnlockService : IEmailUnlockService
    {
        private readonly IExtensionRepository _extensionRepository;
        private readonly ContactRepository _contactRepository;
        private readonly IPitchService _pitchService;
        private readonly DeepSeekPitchService _deepSeekService;
        private readonly QwenPitchService _qwenService;
        private readonly IAiModelSettingsService _aiModelSettings;
        private readonly IPromptSettingsService _promptSettings;
        private readonly IHunterEmailService _hunterService;
        private readonly IProspeoEmailService _prospeoService;

        public EmailUnlockService(
            IExtensionRepository extensionRepository,
            ContactRepository contactRepository,
            IPitchService pitchService,
            DeepSeekPitchService deepSeekService,
            QwenPitchService qwenService,
            IAiModelSettingsService aiModelSettings,
            IPromptSettingsService promptSettings,
            IHunterEmailService hunterService,
            IProspeoEmailService prospeoService)
        {
            _extensionRepository = extensionRepository;
            _contactRepository = contactRepository;
            _pitchService = pitchService;
            _deepSeekService = deepSeekService;
            _qwenService = qwenService;
            _aiModelSettings = aiModelSettings;
            _promptSettings = promptSettings;
            _hunterService = hunterService;
            _prospeoService = prospeoService;
        }

        public async Task<UnlockEmailResult> UnlockAsync(
            ProspeoUnlockRequestDto request,
            bool isAdmin,
            CancellationToken cancellationToken)
        {
            if (request == null || request.ClientID <= 0)
            {
                return UnlockEmailResult.Failed(
                    request?.ContactID,
                    "A valid ClientID is required.");
            }

            // The trace below carries the raw prompt and the raw model reply, so
            // it is built for everyone but handed only to an admin. Whether the
            // caller is one is decided by the caller: this runs both behind an
            // authenticated endpoint and inside a background validation run,
            // and only the former has a User to read a claim from.
            var diagnostics = new UnlockDiagnostics();
            var totalTimer = System.Diagnostics.Stopwatch.StartNew();

            // The address the cache was holding when a forced refresh came in.
            // Reported back so the extension can show what was replaced, and kept
            // out of the AI fallback's candidates so "try again" cannot hand back
            // the same wrong answer. Null on a normal unlock.
            string? rejectedEmail = null;

            UnlockEmailResult Finish(UnlockEmailResult result, string mode, string reason)
            {
                // A later stage can name the mode itself - the AI fallback does
                // when Hunter's address beats the model's - and the caller here
                // cannot know that happened. Only fill in the mode it passed
                // when nothing downstream has claimed one.
                if (string.IsNullOrEmpty(diagnostics.Mode))
                {
                    diagnostics.Mode = mode;
                    diagnostics.ModeReason = reason;
                }

                diagnostics.ElapsedMs = (int)totalTimer.ElapsedMilliseconds;
                result.Diagnostics = isAdmin ? diagnostics : null;
                result.PreviousEmail = rejectedEmail;
                return result;
            }

            void Stage(string name, string outcome, string detail, long elapsedMs) =>
                diagnostics.Stages.Add(new UnlockStageDiagnostics
                {
                    Name = name,
                    Outcome = outcome,
                    Detail = detail,
                    ElapsedMs = (int)elapsedMs
                });

            if (!await _contactRepository.HasAvailableCreditAsync(request.ClientID))
            {
                Stage("credit", "error", "No unlock credit available.", 0);
                return Finish(
                    UnlockEmailResult.NoCredit(
                        request.ContactID,
                        "No unlock credit is available. Please buy credits to unlock this email."),
                    "none",
                    "Stopped before any lookup: the client has no unlock credit.");
            }

            if (string.IsNullOrWhiteSpace(request.LinkedInUrl))
            {
                // The cache is keyed on the LinkedIn URL and Prospeo matches on
                // one, so neither stage can say anything about a contact that
                // has none. The extension always has one; a bulk validation list
                // is full of contacts that do not, and turning those away would
                // fail them for a field the check was never about.
                Stage("cache", "skipped",
                    "No LinkedIn URL, so the 30-day cache could not be checked.", 0);
                Stage("prospeo", "skipped",
                    "No LinkedIn URL, so Prospeo could not be asked.", 0);

                return Finish(
                    await CompleteAiFallbackUnlockAsync(request, diagnostics, null),
                    "ai",
                    "No LinkedIn URL, so the search started at the AI stage.");
            }

            // ------------------------------------------------------- mode 1: cache
            var cacheTimer = System.Diagnostics.Stopwatch.StartNew();
            var cachedEmail = await _extensionRepository.GetProspeoUnlockedEmailAsync(
                request.LinkedInUrl);
            cacheTimer.Stop();

            if (request.ForceRefresh)
            {
                // The caller ticked "look it up again from other sources", so the
                // cached address is read but never served - Prospeo and then the
                // AI fallback answer instead, and whatever they return overwrites
                // the cache row.
                rejectedEmail = string.IsNullOrWhiteSpace(cachedEmail)
                    ? null
                    : cachedEmail.Trim();

                Stage("cache", "skipped",
                    rejectedEmail == null
                        ? "A fresh lookup was requested; the cache held nothing for this URL anyway."
                        : "A fresh lookup was requested, so the cached address '" +
                          rejectedEmail + "' was not served.",
                    cacheTimer.ElapsedMilliseconds);
            }
            else if (!string.IsNullOrWhiteSpace(cachedEmail))
            {
                Stage("cache", "hit",
                    "This LinkedIn URL was unlocked in the last 30 days; no external call was made.",
                    cacheTimer.ElapsedMilliseconds);

                var cachedCompleted = await _extensionRepository.CompleteProspeoUnlockAsync(
                    request.ContactID,
                    request.ClientID,
                    request.LinkedInUrl,
                    cachedEmail);

                var cachedResult = cachedCompleted
                    ? UnlockEmailResult.Succeeded(
                        request.ContactID,
                        cachedEmail,
                        "Email reused from the 30-day unlock cache and one credit deducted.",
                        "cache")
                    : UnlockEmailResult.NoCredit(
                        request.ContactID,
                        "No unlock credit is available. Please buy credits to unlock this email.");

                // Only a cache answer is worth retrying: Prospeo and the AI
                // fallback have already been asked everything they know.
                cachedResult.CanRetryFromOtherSources = cachedCompleted;

                return Finish(cachedResult,
                    "cache",
                    "Served from the 30-day unlock cache. Prospeo and the AI fallback were never called.");
            }
            else
            {
                Stage("cache", "miss",
                    "No unlock for this LinkedIn URL in the last 30 days.",
                    cacheTimer.ElapsedMilliseconds);
            }

            // ----------------------------------------------------- mode 2: Prospeo
            // The call itself lives in IProspeoEmailService, shared with the
            // Audience Assurance email check. Everything around it — the cache,
            // the credit and this trace — is specific to the unlock and stays
            // here.
            var lookup = await _prospeoService.FindEmailAsync(
                request.LinkedInUrl,
                cancellationToken);

            var prospeo = new UnlockProspeoDiagnostics
            {
                ApiKeyConfigured = lookup.ApiKeyConfigured,
                Endpoint = lookup.Endpoint,
                RequestBody = lookup.RequestBody ?? "",
                HttpStatus = lookup.HttpStatus,
                RawResponse = lookup.RawResponse,
                Revealed = lookup.Revealed,
                EmailStatus = lookup.EmailStatus,
                RejectedBecause = lookup.RejectedBecause
            };
            diagnostics.Prospeo = prospeo;

            if (!lookup.Found)
            {
                // "Never asked", "asked and it errored" and "asked and it had
                // nothing" are three different things to whoever reads the
                // trace, so they stay three different stage outcomes.
                var outcome =
                    !lookup.ApiKeyConfigured ? "skipped"
                    : lookup.HttpStatus is null or >= 400 ? "error"
                    : "miss";

                var modeReason =
                    !lookup.ApiKeyConfigured
                        ? "Prospeo was skipped because no API key is configured, so the AI fallback ran."
                    : outcome == "error"
                        ? "The Prospeo call did not complete, so the AI fallback ran."
                        : "Prospeo had no verified address, so the AI fallback ran.";

                Stage("prospeo", outcome,
                    lookup.RejectedBecause ?? "Prospeo returned no usable address.",
                    lookup.ElapsedMs);

                return Finish(
                    await CompleteAiFallbackUnlockAsync(request, diagnostics, rejectedEmail),
                    "ai",
                    modeReason);
            }

            Stage("prospeo", "hit", "Prospeo returned a verified address.", lookup.ElapsedMs);

            var email = lookup.Email!;

            var completed = await _extensionRepository.CompleteProspeoUnlockAsync(
                request.ContactID,
                request.ClientID,
                request.LinkedInUrl,
                email);

            if (!completed)
            {
                return Finish(
                    UnlockEmailResult.NoCredit(
                        request.ContactID,
                        "No unlock credit is available. Please buy credits to unlock this email."),
                    "prospeo",
                    "Prospeo found the address but the credit could not be deducted.");
            }

            var sameAsRejected = rejectedEmail != null &&
                string.Equals(email, rejectedEmail, StringComparison.OrdinalIgnoreCase);

            return Finish(
                UnlockEmailResult.Succeeded(
                    request.ContactID,
                    email,
                    sameAsRejected
                        ? "Prospeo re-verified the same address that was cached, and one credit was deducted."
                        : "Verified email unlocked and one credit deducted."),
                "prospeo",
                sameAsRejected
                    ? "Prospeo was asked again and returned the same verified address the cache held."
                    : "Prospeo returned a verified address, so the AI fallback never ran.");
        }

        public async Task<AiEmailSearchOutcome> FindEmailWithAiCoreAsync(
            FindEmailAiRequestDto request,
            int billingClientId)
        {
            // The instruction lives only in app_prompt_settings (Settings >
            // Admin > Prompts). Nothing is compiled in, so an unsaved prompt
            // ends the search here rather than sending a blank instruction to
            // the model and paying for the reply.
            var template = await _promptSettings.GetPromptAsync(PromptKeys.FindEmail);

            if (string.IsNullOrWhiteSpace(template))
            {
                return new AiEmailSearchOutcome(
                    new PitchResult
                    {
                        IsSuccess = false,
                        Content = "No email research prompt is configured. Add one in Settings > Admin > Prompts."
                    },
                    await _aiModelSettings.GetModelAsync(AiModelPurposes.FindEmail),
                    "",
                    new JArray(),
                    null);
            }

            var finalPrompt = FindEmailPrompt.Build(
                template,
                request.FullName,
                request.JobTitle,
                request.Company,
                request.Location,
                request.ProfileUrl,
                request.CompanyUrl);
            var modelName = await _aiModelSettings.GetModelAsync(AiModelPurposes.FindEmail);
            var enquiryRequest = new EnquiryRequest
            {
                Prompt = finalPrompt,
                ScrappedData = "",
                ModelName = modelName
            };
            var searchResult = IsDeepSeekModel(modelName)
                ? await _deepSeekService.GenerateWebSearchAsync(enquiryRequest, billingClientId)
                : IsQwenModel(modelName)
                    ? await _qwenService.GenerateWebSearchAsync(enquiryRequest, billingClientId)
                    : await _pitchService.GenerateWebSearchAsync(enquiryRequest, billingClientId);
            var results = searchResult.IsSuccess
                ? ParseFindEmailResults(searchResult.Content ?? "")
                : new JArray();
            var company = searchResult.IsSuccess
                ? ParseFindEmailCompany(searchResult.Content ?? "")
                : null;

            return new AiEmailSearchOutcome(
                searchResult,
                modelName,
                finalPrompt,
                results,
                company);
        }

        /// <summary>
        /// Mode 3, and mode 4 behind it.
        ///
        /// Runs the AI search first. When the model's best candidate falls below
        /// the Hunter confidence threshold - or the model produced nothing at
        /// all - Hunter.io is asked as well and whichever answer scores higher
        /// is the one returned. A confident model answer never spends a Hunter
        /// request.
        ///
        /// Fills in <paramref name="diagnostics"/> as it goes so an admin can
        /// see the prompt, the model's raw reply, what Hunter said and which
        /// answer won.
        /// </summary>
        private async Task<UnlockEmailResult> CompleteAiFallbackUnlockAsync(
            ProspeoUnlockRequestDto request,
            UnlockDiagnostics diagnostics,
            string? rejectedEmail = null)
        {
            var aiRequest = new FindEmailAiRequestDto
            {
                ClientId = request.ClientID,
                FullName = request.Name,
                JobTitle = request.JobTitle,
                Company = request.CompanyName,
                Location = request.Location,
                ProfileUrl = request.LinkedInUrl,
                CompanyUrl = request.CompanyUrl ?? request.Domain
            };

            var aiTimer = System.Diagnostics.Stopwatch.StartNew();

            // Zero prevents the model service from deducting. Completion below
            // atomically deducts once and writes UnlockedContacts only after a
            // usable email has actually been returned.
            var aiSearch = await FindEmailWithAiCoreAsync(aiRequest, 0);
            aiTimer.Stop();

            // Contact data rather than a trace, so it rides on every result this
            // method returns - including the failures. A search that found no
            // address still learned who the person works for, and that is worth
            // saving whether or not the email came back.
            var companyDetails = aiSearch.Company == null
                ? null
                : new UnlockCompanyDetails
                {
                    Website = aiSearch.Company.Website,
                    Industry = aiSearch.Company.Industry,
                    Size = aiSearch.Company.Size
                };

            var ai = new UnlockAiDiagnostics
            {
                Provider = ProviderLabel(aiSearch.ModelName),
                Model = aiSearch.ModelName ?? "",
                Prompt = aiSearch.FinalPrompt ?? "",
                Raw = aiSearch.SearchResult?.Content ?? "",
                Results = ToJsonNode(aiSearch.Results),
                // Its own instance, not the one returned below: that one can
                // pick up a website from Hunter, and this section reports what
                // the model itself said.
                Company = aiSearch.Company == null ? null : new UnlockCompanyDetails
                {
                    Website = aiSearch.Company.Website,
                    Industry = aiSearch.Company.Industry,
                    Size = aiSearch.Company.Size
                },
                IsSuccess = aiSearch.SearchResult?.IsSuccess ?? false,
                Usage = aiSearch.SearchResult == null ? null : new UnlockAiUsage
                {
                    PromptTokens = aiSearch.SearchResult.PromptTokens,
                    CompletionTokens = aiSearch.SearchResult.CompletionTokens,
                    SearchTokens = aiSearch.SearchResult.SearchTokens,
                    TotalTokens = aiSearch.SearchResult.TotalTokens,
                    CurrentCost = aiSearch.SearchResult.CurrentCost
                }
            };
            diagnostics.Ai = ai;

            void Stage(string name, string outcome, string detail, int elapsedMs) =>
                diagnostics.Stages.Add(new UnlockStageDiagnostics
                {
                    Name = name,
                    Outcome = outcome,
                    Detail = detail,
                    ElapsedMs = elapsedMs
                });

            string? aiEmail = null;
            var aiConfidence = 0;

            if (!aiSearch.SearchResult.IsSuccess)
            {
                // The model failing is no longer the end of the road: Hunter is
                // still worth asking, so this records the failure and carries on.
                ai.ChoiceReason = "The model call itself failed.";
                Stage("ai", "error",
                    "The " + ai.Provider + " call failed (" + ai.Model + ").",
                    (int)aiTimer.ElapsedMilliseconds);
            }
            else
            {
                // On a forced refresh the cached address is the one the caller is
                // telling us is wrong, so it sorts last - it is still kept,
                // because a wrong-looking address beats returning nothing at all.
                // After that a direct address beats a guessed pattern, then the
                // model's own confidence decides, and ties keep the order the
                // model returned.
                var ranked = aiSearch.Results
                    .OfType<JObject>()
                    .Select((item, index) => new
                    {
                        Email = item["email"]?.Value<string>()?.Trim(),
                        Type = item["type"]?.Value<string>() ?? "",
                        Confidence = item["confidence"]?.Value<int>() ?? 0,
                        Index = index
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.Email) &&
                        System.Net.Mail.MailAddress.TryCreate(item.Email, out _))
                    .OrderBy(item => rejectedEmail != null &&
                        string.Equals(item.Email, rejectedEmail, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(item =>
                        string.Equals(item.Type, "direct", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(item => item.Confidence)
                    .ThenBy(item => item.Index)
                    .ToList();

                var chosen = ranked.FirstOrDefault();

                if (chosen == null || string.IsNullOrWhiteSpace(chosen.Email))
                {
                    ai.ChoiceReason = aiSearch.Results.Count == 0
                        ? "The model returned no candidates."
                        : "The model returned " + aiSearch.Results.Count +
                            " candidate(s), none of them a valid address.";
                    Stage("ai", "miss", ai.ChoiceReason, (int)aiTimer.ElapsedMilliseconds);
                }
                else
                {
                    aiEmail = chosen.Email;
                    aiConfidence = chosen.Confidence;

                    ai.ChosenEmail = aiEmail;
                    ai.ChoiceReason = "Picked from " + ranked.Count +
                        " valid candidate(s): type '" + chosen.Type +
                        "', confidence " + aiConfidence + ".";
                    Stage("ai", "hit", ai.ChoiceReason, (int)aiTimer.ElapsedMilliseconds);
                }
            }

            // ------------------------------------------------------ mode 4: Hunter
            var threshold = _hunterService.ConfidenceThreshold;
            string? hunterEmail = null;
            var hunterScore = 0;
            UnlockHunterDiagnostics? hunterDiagnostics = null;

            if (aiEmail != null && aiConfidence >= threshold)
            {
                Stage("hunter", "skipped",
                    "The model was " + aiConfidence + "% confident, at or above the " +
                    threshold + "% threshold, so Hunter was not called.",
                    0);
            }
            else
            {
                var triggerReason = aiEmail == null
                    ? "The AI stage produced no usable address."
                    : "The model was only " + aiConfidence + "% confident, below the " +
                      threshold + "% threshold.";

                var lookup = await _hunterService.FindEmailAsync(new HunterLookupRequest
                {
                    FullName = request.Name,

                    // The AI search is what actually researched this person, so
                    // its website is the domain to trust. What the extension
                    // scraped comes last: on a profile with no website on the
                    // page it is the company's LinkedIn URL, and Hunter cannot
                    // find anybody at linkedin.com.
                    AiWebsite = aiSearch.Company?.Website,

                    // A low-confidence guess is still usually right about the
                    // employer, so its domain stands in when the model reported
                    // no website.
                    EmailHint = aiEmail,
                    Domain = request.Domain,
                    CompanyUrl = request.CompanyUrl,
                    Company = request.CompanyName
                });

                hunterDiagnostics = new UnlockHunterDiagnostics
                {
                    ApiKeyConfigured = lookup.ApiKeyConfigured,
                    Endpoint = lookup.Endpoint,
                    RequestUrl = lookup.RequestUrl,
                    HttpStatus = lookup.HttpStatus,
                    RawResponse = lookup.RawResponse,
                    TriggeredAtConfidence = aiConfidence,
                    ConfidenceThreshold = threshold,
                    TriggerReason = triggerReason,
                    Email = lookup.Email,
                    Score = lookup.Score,
                    VerificationStatus = lookup.VerificationStatus,
                    Domain = lookup.Domain,
                    DomainSource = lookup.DomainSource,
                    Position = lookup.Position,
                    SourceCount = lookup.SourceCount,
                    RejectedBecause = lookup.RejectedBecause
                };
                diagnostics.Hunter = hunterDiagnostics;

                if (lookup.Found)
                {
                    hunterEmail = lookup.Email;
                    hunterScore = lookup.Score;
                }

                // "skipped" is for the stage never reaching Hunter at all - no key,
                // or nothing to search with. Once a request went out, an answer
                // without an address is a miss and a bad response is an error.
                // An address Hunter scores below the threshold is a miss too: it
                // is kept for the trace but cannot be served on its own.
                var outcome =
                    lookup.Found && lookup.Score >= threshold ? "hit"
                    : lookup.Found ? "miss"
                    : !lookup.ApiKeyConfigured ? "skipped"
                    : string.IsNullOrEmpty(lookup.RequestUrl) ? "skipped"
                    : lookup.HttpStatus is >= 200 and < 300 ? "miss"
                    : "error";

                Stage("hunter", outcome,
                    triggerReason + " " +
                    (!lookup.Found
                        ? lookup.RejectedBecause ?? "Hunter returned nothing usable."
                        : lookup.Score >= threshold
                            ? "Hunter returned " + hunterEmail + " with a score of " +
                              hunterScore + "."
                            : "Hunter returned " + hunterEmail + " but scored it " +
                              hunterScore + "%, below the " + threshold +
                              "% threshold, so it cannot be served on its own."),
                    lookup.ElapsedMs);
            }

            // Hunter is asked about a domain, so a lookup that got that far knows
            // the company website even when the model did not report one. Only
            // ever fills a gap - what the model found is the better answer,
            // because it is a homepage rather than a bare mail domain.
            if (!string.IsNullOrWhiteSpace(hunterDiagnostics?.Domain) &&
                string.IsNullOrWhiteSpace(companyDetails?.Website))
            {
                companyDetails ??= new UnlockCompanyDetails();
                companyDetails.Website = hunterDiagnostics.Domain;
            }

            // Nothing found at all is reported as nothing, not as an empty shape
            // the extension has to test three fields of.
            if (companyDetails?.HasAny != true)
                companyDetails = null;

            UnlockEmailResult WithCompany(UnlockEmailResult result)
            {
                result.Company = companyDetails;
                return result;
            }

            // Both carried on every answer this method produces, so the caller
            // can say how sure the search was without knowing which stage won.
            UnlockEmailResult WithConfidence(UnlockEmailResult result, int confidence)
            {
                result.Confidence = confidence;
                result.ConfidenceThreshold = threshold;
                return result;
            }

            // Hunter's score and the model's confidence are both 0-100 statements
            // about the same address, so the higher one wins. A tie keeps the AI
            // answer, which already survived the ranking above.
            var preferHunter = hunterEmail != null &&
                (aiEmail == null || hunterScore > aiConfidence);

            // A forced refresh means the caller has called one address wrong.
            // Whichever stage repeats it loses to a stage offering something else.
            if (rejectedEmail != null && aiEmail != null && hunterEmail != null)
            {
                var aiRepeats = SameAddress(aiEmail, rejectedEmail);
                var hunterRepeats = SameAddress(hunterEmail, rejectedEmail);

                if (aiRepeats != hunterRepeats)
                    preferHunter = aiRepeats;
            }

            var email = preferHunter ? hunterEmail : aiEmail;

            if (hunterDiagnostics != null)
            {
                hunterDiagnostics.Preferred = preferHunter;
                hunterDiagnostics.ComparisonReason =
                    hunterEmail == null && aiEmail == null
                        ? "Neither stage produced an address."
                    : hunterEmail == null
                        ? "Hunter had nothing to compare, so the model's answer stands."
                    : aiEmail == null
                        ? "The model had no candidate, so Hunter's answer is the only one."
                    : preferHunter
                        ? "Hunter scored " + hunterScore + " against the model's " +
                          aiConfidence + ", so Hunter's address was used."
                        : "The model's " + aiConfidence + " was not beaten by Hunter's " +
                          hunterScore + ", so the model's address was used.";
            }

            // How sure the winning stage is of its own answer, on the 0-100 scale
            // both stages use. Reported to the caller rather than acted on: an
            // address the search is only half sure of is still worth showing, as
            // long as the person deciding what to do with it is told as much.
            // The threshold still decides whether Hunter is asked at all.
            var chosenConfidence = preferHunter ? hunterScore : aiConfidence;

            if (email != null && chosenConfidence < threshold)
            {
                Stage("confidence", "miss",
                    "The best address found was " + email + " at " + chosenConfidence +
                    "%, below the " + threshold + "% mark, so it is returned with its " +
                    "confidence shown rather than presented as confirmed.",
                    0);
            }

            // Nothing to return means every stage that ran came back empty.
            // Hunter always runs when the AI stage does not clear the threshold.
            if (string.IsNullOrWhiteSpace(email))
            {
                await ForgetRejectedCacheEntryAsync(request, rejectedEmail, diagnostics);

                diagnostics.Mode = "none";
                diagnostics.ModeReason =
                    "Prospeo, the AI fallback and Hunter all came back empty.";

                return WithCompany(UnlockEmailResult.Failed(
                    request.ContactID,
                    "No email address was found by Prospeo, the AI fallback or Hunter. No credit was deducted."));
            }

            var repeatedRejected = SameAddress(email, rejectedEmail);

            if (repeatedRejected && !preferHunter)
            {
                ai.ChoiceReason += " This is the same address the cache held; the fresh lookup found nothing else.";
            }

            // Hunter winning makes this a mode 4 unlock, and the caller cannot
            // know that - it only knows it handed control to the AI fallback.
            if (preferHunter)
            {
                diagnostics.Mode = "hunter";
                diagnostics.ModeReason =
                    "The model was not confident enough, so Hunter was asked and its " +
                    "address scored higher.";
            }

            var completed = await CompleteUnlockAsync(request, email);

            if (!completed)
            {
                return WithConfidence(WithCompany(UnlockEmailResult.NoCredit(
                    request.ContactID,
                    "Email was found, but unlock could not complete because credit was unavailable.")),
                    chosenConfidence);
            }

            var foundBy = preferHunter ? "Hunter" : "AI fallback";

            return WithConfidence(WithCompany(UnlockEmailResult.Succeeded(
                request.ContactID,
                email,
                repeatedRejected
                    ? "The fresh lookup returned the same address that was cached, and one credit was deducted."
                    : "Email found by " + foundBy + ", unlock history saved and one credit deducted.",
                preferHunter ? "hunter" : "ai")),
                chosenConfidence);
        }

        /// <summary>
        /// Deducts the credit and records the unlock.
        ///
        /// The unlock ledger is keyed on the LinkedIn URL, so a contact without
        /// one cannot have a row: writing one would key every such contact to
        /// the same empty string, and the next lookup would be handed somebody
        /// else's address. Those deduct the credit on its own and leave no cache
        /// entry behind, which also means they are researched again next time.
        /// </summary>
        private async Task<bool> CompleteUnlockAsync(ProspeoUnlockRequestDto request, string email)
        {
            if (string.IsNullOrWhiteSpace(request.LinkedInUrl))
                return await _contactRepository.CreditDeduction(request.ClientID);

            return await _extensionRepository.CompleteProspeoUnlockAsync(
                request.ContactID,
                request.ClientID,
                request.LinkedInUrl,
                email);
        }

        /// <summary>Two addresses being the same one, case aside.</summary>
        private static bool SameAddress(string? left, string? right) =>
            !string.IsNullOrWhiteSpace(left) &&
            !string.IsNullOrWhiteSpace(right) &&
            string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A forced refresh that found nothing leaves the cache holding the very
        /// address the caller called wrong, and the next plain unlock would serve
        /// it straight back. Blank it instead - the unlock row stays for the
        /// history, only the address goes.
        /// </summary>
        private async Task ForgetRejectedCacheEntryAsync(
            ProspeoUnlockRequestDto request,
            string? rejectedEmail,
            UnlockDiagnostics diagnostics)
        {
            if (string.IsNullOrWhiteSpace(rejectedEmail))
                return;

            var cleared = await _extensionRepository.ClearProspeoUnlockedEmailAsync(
                request.LinkedInUrl,
                rejectedEmail);

            diagnostics.Stages.Add(new UnlockStageDiagnostics
            {
                Name = "cache",
                Outcome = cleared ? "cleared" : "skipped",
                Detail = cleared
                    ? "The fresh lookup found nothing, so the rejected address '" +
                      rejectedEmail + "' was dropped from the cache."
                    : "The rejected address was no longer in the cache.",
                ElapsedMs = 0
            });
        }

        private static bool IsDeepSeekModel(string? modelName)
            => modelName?.StartsWith("deepseek-", StringComparison.OrdinalIgnoreCase) == true;

        private static bool IsQwenModel(string? modelName)
            => modelName?.StartsWith("qwen", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>
        /// The provider label reported in the unlock diagnostics. Derived from
        /// the model name rather than stored, so it stays correct whichever
        /// model an admin picks for the purpose.
        /// </summary>
        public static string ProviderLabel(string? modelName) =>
            IsDeepSeekModel(modelName) ? "DeepSeek"
            : IsQwenModel(modelName) ? "Qwen"
            : "OpenAI";

        /// <summary>
        /// The instruction asks for bare JSON, but models still wrap it in a
        /// ```json fence or add a sentence around it, so pull out the object and
        /// return its "results" array. Returns an empty array when the answer
        /// cannot be parsed — the raw text is always returned alongside.
        /// </summary>
        private static JArray ParseFindEmailResults(string content)
            => ExtractJsonObject(content)?["results"] as JArray ?? new JArray();

        /// <summary>
        /// The "company" block the same instruction asks for: the employer's
        /// website, industry and headcount band. Every field is null when the
        /// model could not source it, and the whole thing is null when the reply
        /// carried no company block at all.
        /// </summary>
        private static AiCompanyDetails? ParseFindEmailCompany(string content)
        {
            if (ExtractJsonObject(content)?["company"] is not JObject company)
                return null;

            return new AiCompanyDetails(
                Value(company["website"]),
                Value(company["industry"]),
                Value(company["size"]));

            // A model that has nothing to report writes JSON null, but "null"
            // and "Not provided" as strings both turn up too.
            static string? Value(JToken? token)
            {
                var text = token?.Type == JTokenType.Null
                    ? null
                    : token?.Value<string>()?.Trim();

                return string.IsNullOrWhiteSpace(text) ||
                       text.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                       text.Equals(FindEmailPrompt.MissingValue, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : text;
            }
        }

        /// <summary>
        /// Pulls the JSON object out of a model reply, whether it arrived bare,
        /// inside a ```json fence, or surrounded by a sentence of prose. Returns
        /// null when there is nothing parseable — the raw text is always
        /// returned to the caller alongside.
        /// </summary>
        private static JObject? ExtractJsonObject(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            var text = content.Trim();

            // Strip a leading ```json / ``` fence and its closing fence.
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                int firstLineBreak = text.IndexOf('\n');
                if (firstLineBreak >= 0)
                    text = text[(firstLineBreak + 1)..];

                int closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (closingFence >= 0)
                    text = text[..closingFence];

                text = text.Trim();
            }

            // Fall back to the outermost { ... } when prose surrounds the JSON.
            if (!text.StartsWith("{", StringComparison.Ordinal))
            {
                int start = text.IndexOf('{');
                int end = text.LastIndexOf('}');
                if (start < 0 || end <= start)
                    return null;

                text = text[start..(end + 1)];
            }

            try
            {
                return JsonConvert.DeserializeObject<JObject>(text);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Re-parses a Newtonsoft array through System.Text.Json so the response
        /// serialiser renders it as a real array rather than JToken internals.
        /// </summary>
        private static System.Text.Json.Nodes.JsonNode? ToJsonNode(JArray? value)
        {
            if (value == null)
                return null;

            try
            {
                return System.Text.Json.Nodes.JsonNode.Parse(value.ToString(Formatting.None));
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
    }
}
