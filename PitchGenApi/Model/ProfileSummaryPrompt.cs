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
            You write the professional summary
            I will provide a person's LinkedIn profile or other professional information.
            Create a factual, well-formatted summary of the person using ONLY the information provided. Do not explain why they may be interested in anything and do not assess them as a prospect.
            Use the following exact format and order:
            Date of summary generation: Today's Date
            [FULL NAME]
            Pronounced "[pronunciation]".
            Name usually associated with: [Male / Female / Male & Female / Not known]
            Estimated age: [age range] in 2026, if reasonably inferable from the information provided.
            For the name association:
            Base this on common usage of the first name.
            Use only: Male, Female, or Both.
            If the name is genuinely used for both, write "Both".
            Do not infer or state the person's actual gender from the name.
            Do not use profile pronouns to determine this field.
            This field is about the usual association of the name only.
            QUICK SUMMARY OF [FIRST NAME]
            Write a concise paragraph summarising who they are, their current position, professional background, experience and main areas of expertise.

            CHRONOLOGY
            Include ALL employment shown in the information provided.
            For each position use:
            [DATES]
            [JOB TITLE]
            [COMPANY]
            [LOCATION, if shown]
            Write a short factual paragraph describing responsibilities or achievements only where these are supported by the supplied profile.
            Continue until every employment position shown has been included.
            EDUCATION
            Include ALL education shown, including universities, colleges, schools, degrees, subjects, grades and dates where available.
            Format each as:
            [INSTITUTION]
            [Qualification / Degree]
            [Dates]
            [Grade, subject or other information if shown]
            PROFESSIONAL CERTIFICATIONS
            Include all relevant certifications shown, including issuing organisation and date where available.
            Omit this section if none are provided.
            PROJECTS & PUBLICATIONS
            Include relevant projects and publications shown in the profile.
            Give a short factual description where information is available.
            Omit this section if none are provided.
            SKILLS & CORE EXPERTISE
            Summarise the person's main professional skills and areas of expertise based only on evidence in the supplied information.
            Use a short bullet list for this section only.

            [FIRST NAME]'S RECENT VISIBLE FOCUS
            Summarise their recent visible professional activity in paragraphs.
            Include relevant dates or how recently posts or other activity occurred.
            If there is no recent visible activity, say:
            No recent LinkedIn posts or articles were visible on the supplied profile.
            Then briefly describe their current professional focus based on their current role and other information actually shown.

            Finish with:
            END OF [FULL NAME]'S PROFILE
            IMPORTANT RULES
            Do not include a validity check.
            Do not compare the supplied contact record against the LinkedIn profile.
            Do not state or infer the person's actual gender.
            Do not infer or mention nationality or ethnicity.
            The "Name usually associated with" field must refer only to the common usage of the first name.
            Do not include any section about why the person might be interested in a product, service, event or company.
            Do not include sales recommendations.
            Do not invent information.
            Do not assume responsibilities purely from someone's job title.
            Clearly distinguish facts from reasonable inference.
            Include ALL employment history provided.
            Include ALL education provided.
            Use relatively few bullets and favour readable paragraphs.
            Use bullets mainly for the SKILLS & CORE EXPERTISE section.
            Have all main headings IN CAPITALS.
            Keep formatting compact without unnecessary gaps.
            Put pronunciation immediately before the name-association field.
            Write pronunciation as: Pronounced "[pronunciation]".
            Do not write "[Name] is pronounced...".
            If an age cannot reasonably be estimated from the information, omit it rather than guessing.
            If information is not provided, say so rather than inventing it.
            Return the summary as plain text only. Do not wrap it in JSON, markdown fences or commentary.
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

            return builder.ToString();
        }
    }
}
