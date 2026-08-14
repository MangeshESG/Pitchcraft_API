namespace PitchGenApi.Model
{
    using System.Text;

    /// <summary>
    /// The instruction behind POST /api/Extension/EX_profile-summary.
    ///
    /// Like <see cref="FindEmailPrompt"/> it lives in code, not in the request
    /// payload and not in the database: the extension supplies only the scraped
    /// profile text, never the instruction, so every caller gets the same
    /// summary format whichever model an admin points the purpose at.
    /// </summary>
    public static class ProfileSummaryPrompt
    {
        /// <summary>
        /// Sent as the system instruction. The web-search services put this in
        /// the "instructions"/system slot and the profile text in the user slot,
        /// so it reads the same way to OpenAI and to DeepSeek.
        /// </summary>
        public const string Instruction = """
            You write the professional summary.
            I will provide a person's LinkedIn profile or other professional information.
            Create a factual summary of the person using ONLY the information provided. Do not explain why they may be interested in anything and do not assess them as a prospect.

            OUTPUT FORMAT
            Return a single JSON object and nothing else. No markdown fences, no commentary, no leading or trailing text.
            Use exactly these keys, in this order, with these types:

            {
              "generatedOn": "YYYY-MM-DD",
              "fullName": "string",
              "firstName": "string",
              "pronunciation": "string",
              "nameUsuallyAssociatedWith": "Male" | "Female" | "Both" | "Unknown",
              "estimatedAge": "string or null",
              "headline": "string or null",
              "currentJobTitle": "string or null",
              "currentCompany": "string or null",
              "location": "string or null",
              "quickSummary": "string",
              "chronology": [
                {
                  "dates": "string",
                  "jobTitle": "string",
                  "company": "string",
                  "location": "string or null",
                  "description": "string or null"
                }
              ],
              "education": [
                {
                  "institution": "string",
                  "qualification": "string or null",
                  "dates": "string or null",
                  "details": "string or null"
                }
              ],
              "certifications": [
                {
                  "name": "string",
                  "issuer": "string or null",
                  "date": "string or null"
                }
              ],
              "projectsAndPublications": [
                {
                  "title": "string",
                  "description": "string or null"
                }
              ],
              "skills": ["string"],
              "recentVisibleFocus": {
                "hasRecentActivity": true | false,
                "paragraphs": ["string"]
              },
              "notProvided": ["string"]
            }

            FIELD RULES
            generatedOn: today's date in YYYY-MM-DD form.
            fullName: the person's full name as shown. firstName: their first name only.
            pronunciation: the phonetic pronunciation of the full name, e.g. "JAY-mee BAN-iss-ter". Give the pronunciation text only, with no "Pronounced" wording and no surrounding quotes inside the value.
            nameUsuallyAssociatedWith: one of exactly "Male", "Female", "Both" or "Unknown", written with that capitalisation and nothing else.
              Base this on common usage of the first name.
              Use "Both" if the name is genuinely used for both.
              Use "Unknown" if the common usage of the name cannot be established.
              Do not infer or state the person's actual gender from the name.
              Do not use profile pronouns to determine this field.
              This field is about the usual association of the name only.
            estimatedAge: an age range with the year, e.g. "35-40 in 2026", only if reasonably inferable from the information provided. Use null rather than guessing.
            headline, currentJobTitle, currentCompany, location: taken from the profile where shown, otherwise null.
            quickSummary: one concise paragraph as plain prose summarising who they are, their current position, professional background, experience and main areas of expertise. No bullets, no headings, no line breaks.
            chronology: every employment position shown in the information provided, most recent first. description is a short factual paragraph on responsibilities or achievements, only where the supplied profile supports it, otherwise null. Continue until every position shown has been included.
            education: all education shown, including universities, colleges, schools, degrees, subjects, grades and dates where available. details carries grade, subject or other information shown.
            certifications: all relevant certifications shown, with issuing organisation and date where available.
            projectsAndPublications: relevant projects and publications shown, with a short factual description where information is available.
            skills: a short list of the person's main professional skills and areas of expertise, based only on evidence in the supplied information. Each item is a short phrase, not a sentence.
            recentVisibleFocus: hasRecentActivity is true when recent posts, reposts or articles were visible. paragraphs holds the summary of that activity as one or more plain prose paragraphs, including relevant dates or how recently the activity occurred.
              If no recent activity was visible, set hasRecentActivity to false and make the first paragraph exactly: "No recent LinkedIn posts or articles were visible on the supplied profile." Then add a paragraph briefly describing their current professional focus based on their current role and other information actually shown.
            notProvided: short labels for the sections the supplied profile did not cover, e.g. "Education", "Certifications". Use an empty array when everything was provided.

            EMPTY VALUES
            Return every key, always. Never omit a key.
            Use an empty array [] for chronology, education, certifications, projectsAndPublications, skills and notProvided when there is nothing to report.
            Use null for absent string values. Never write "N/A", "None", "Not provided" or similar inside a string field.

            IMPORTANT RULES
            Do not include a validity check.
            Do not compare the supplied contact record against the LinkedIn profile.
            Do not state or infer the person's actual gender.
            Do not infer or mention nationality or ethnicity.
            The nameUsuallyAssociatedWith field must refer only to the common usage of the first name.
            Do not include anything about why the person might be interested in a product, service, event or company.
            Do not include sales recommendations.
            Do not invent information.
            Do not assume responsibilities purely from someone's job title.
            Clearly distinguish facts from reasonable inference.
            Include ALL employment history provided.
            Include ALL education provided.
            Do not use markdown inside any string value: no asterisks, no hash headings, no bullet characters.
            Where the profile's own dates overlap or conflict, reproduce them as supplied rather than reconciling them, and say so in the relevant description.
            If information is not provided, leave the field null or the array empty rather than inventing it.
            Return raw JSON only, starting with { and ending with }.
            """;

        /// <summary>
        /// The user half of the call: the identifying fields the panel captured,
        /// followed by the visible profile text.
        /// </summary>
        public static string Build(
            string? contactName,
            string? companyName,
            string? linkedInUrl,
            string profileText)
        {
            var contextLines = new List<string>();

            if (!string.IsNullOrWhiteSpace(contactName))
                contextLines.Add($"Contact name: {contactName!.Trim()}");

            if (!string.IsNullOrWhiteSpace(companyName))
                contextLines.Add($"Company: {companyName!.Trim()}");

            if (!string.IsNullOrWhiteSpace(linkedInUrl))
                contextLines.Add($"LinkedIn: {linkedInUrl!.Trim()}");

            var builder = new StringBuilder();

            if (contextLines.Count > 0)
                builder.AppendLine(string.Join("\n", contextLines)).AppendLine();

            builder.AppendLine("LinkedIn profile text:").AppendLine(profileText);

            // Reinforced in the user slot as well: some providers weight the
            // system instruction loosely and drift back into prose.
            builder
                .AppendLine()
                .AppendLine("Return a single raw JSON object using the required schema. No markdown fences, no commentary.");

            return builder.ToString();
        }
    }
}
