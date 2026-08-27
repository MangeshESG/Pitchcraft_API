namespace PitchGenApi.Model
{
    /// <summary>
    /// The static demo instruction behind POST /api/Extension/find-email-AI.
    ///
    /// It lives in code, not in the request payload and not in the database:
    /// callers supply only the contact details, never the instruction, so how
    /// the search is performed is fixed for every caller.
    /// </summary>
    public static class FindEmailPrompt
    {
        /// <summary>Shown in place of any input the caller did not send.</summary>
        public const string MissingValue = "Not provided";

        /// <summary>
        /// Every placeholder the instruction supports. The value is whatever the
        /// caller sent for that field, or <see cref="MissingValue"/>.
        /// The "{job_ title}" form is accepted too because that spelling is in
        /// circulation in existing prompt copies.
        /// </summary>
        private static readonly string[] JobTitleTokens = { "{job_title}", "{job_ title}" };

        public const string DemoInstruction = """
Find the most likely current professional email address(es) for:
Name: {full_name}
Role: {job_title}
Company: {company}
Location: {location}
LinkedIn/Profile URL:{profile_url}
Company URL:{company_url}

Your goal is to achieve the highest possible confidence using every lawful, publicly accessible, or otherwise authorised source available.
Search deeply and systematically. Do not stop after finding the first plausible result.
SEARCH ALL RELEVANT SOURCES, INCLUDING:
OFFICIAL COMPANY SOURCES
Company homepage
Contact page
About/team/leadership pages
Staff directories
Press releases
News/blog posts
Author pages
Partner pages
Customer/case-study pages
Event pages
Webinar pages
Speaker pages
Careers pages
Investor relations pages
Privacy/legal/terms pages
Sitemap
Downloadable PDFs
Brochures
Prospectuses
Whitepapers
Media kits
Conference documents
Presentations
Publicly indexed files
Subdomains
Historical company domains
Previous company names
Rebrands, mergers and acquisitions
SEARCH ENGINES
Search combinations including:
"[Full Name]" email
"[Full Name]" "[Company]"
"[Full Name]" contact
"[Full Name]" email address
"[Full Name]" "@companydomain.com"
"[Full Name]" filetype:pdf
"[Full Name]" filetype:ppt
"[Full Name]" filetype:doc
site:companydomain.com "[Full Name]"
site:companydomain.com "@companydomain.com"
site:companydomain.com "[surname]"
site:companydomain.com email
exact candidate emails in quotation marks
PROFESSIONAL NETWORKS AND PROFILES
LinkedIn
Company LinkedIn pages
Executive bios
Professional association profiles
Industry directories
Board/member profiles
Alumni profiles
University profiles
GitHub
Personal professional websites
Portfolio sites
Substack
Medium
Speaker/profile platforms
CONFERENCES AND EVENTS
Conference websites
Speaker bios
Event agendas
Webinar pages
Meetup/event pages
Expo exhibitor pages
Sponsor pages
Panelist profiles
Podcast guest pages
Virtual event platforms
Conference PDFs
Archived event programs
PRESS AND MEDIA
PR Newswire
Business Wire
GlobeNewswire
Company press releases
News articles
Interviews
Trade publications
Industry publications
Podcast show notes
Media contact pages
PUBLIC BUSINESS SOURCES
Government corporate registries
SEC or equivalent filings where relevant
Companies House
State corporation registries
Charity/nonprofit filings
Public procurement records
Public tenders
Government supplier directories
Chamber of Commerce listings
Public licensing databases
Public professional registers where relevant
BUSINESS / CONTACT DATABASES
Use as supporting evidence where accessible:
RocketReach
ContactOut
Apollo
ZoomInfo
Hunter
SignalHire
Lusha
Seamless.AI
LeadIQ
Adapt
UpLead
Datanyze
GetProspect
GetLatka
FinalScout
The Org
Crunchbase
PitchBook profiles where publicly visible
Lead411
SalesIntel
People Data Labs results where publicly exposed
Any other reputable business-contact database
EMAIL DISCOVERY / PATTERN SOURCES
Hunter domain search
Email-format databases
Publicly exposed employee emails
Known company email-format pages
Multiple employee email examples from the same domain
ARCHIVES AND HISTORICAL SOURCES
Internet Archive / Wayback Machine
Search-engine cached/indexed pages
Archived company pages
Old press releases
Historical PDFs
Previous company domains
Old conference pages
Old staff directories
Historical contact pages
DOCUMENT SEARCH
Search public:
PDFs
PowerPoints
Word documents
CSVs where relevant
conference brochures
media kits
exhibitor lists
sponsorship packs
research papers
whitepapers
prospectuses
newsletters
public CRM-generated landing pages
PARTNER / THIRD-PARTY ORGANISATION SOURCES
Technology partners
Resellers
Vendors
Customers
Sponsors
Investors
Accelerators
Incubators
Professional associations
Universities
Charities
Industry bodies
Government partners
SOCIAL / PUBLIC PROFESSIONAL CONTENT
Where professionally relevant and publicly accessible:
LinkedIn posts
X/Twitter bios or posts
YouTube descriptions
Podcast descriptions
Professional Facebook pages
Substack profiles
Medium author pages
EMAIL VALIDATION PROCESS
Confirm the person's identity and current company/role.
Identify the company's:
Current domain
Previous domains
Email domains
Rebrands
Acquisitions
Historical names
Find known employee email addresses from the same company.
Determine established email patterns, such as:
firstname@domain.com
firstname.lastname@domain.com
firstinitiallastname@domain.com
firstname_lastname@domain.com
lastname@domain.com
Generate candidate emails only after finding evidence for the company's naming convention.
Search every candidate email exactly in quotation marks.
Look for independent confirmation across multiple unrelated sources.
Treat evidence according to strength:
Strongest:
Exact full email published by the company
Exact full email published by the person
Exact full email in an official PDF/document
Exact full email on an authoritative partner/event page
Strong:
Exact full email independently published by several reputable sources
Exact email plus confirmed company pattern
Supporting:
Masked business-database email
Confirmed company email format
Matching employee patterns
Weak:
Purely generated email
Generic pattern guess
Uncorroborated database result
Never describe an address as verified unless the complete email is actually displayed by a credible source.
Masked addresses count only as supporting evidence.
Do not infer hidden characters from masked addresses and then call the result verified.
Check whether the address may be historical or inactive.
Prefer current professional addresses over old addresses.
If the current company does not expose a direct address, an older verified professional address may still be included but must have lower confidence.
Do not use leaked, hacked, stolen, breached, private, or improperly obtained datasets.
COMPANY DETAILS
While researching the person, also capture three facts about the company they currently work for.
Use the same sources listed above. The company website, the company LinkedIn page, the About page and public business registries are usually enough.
Website: the official homepage of the company, as a full URL. Not a LinkedIn page, not a careers portal, not a job board listing, not a parent company unless the person works for the parent.
Industry: the primary industry in a few words, for example "Commercial Real Estate", "SaaS - Marketing Automation", "Renewable Energy". Prefer the wording the company itself or its LinkedIn page uses.
Size: the current employee headcount as one of these bands exactly: "1-10", "11-50", "51-200", "201-500", "501-1000", "1001-5000", "5001-10000", "10001+". Use the band the LinkedIn company page shows when it is available.
Return null for any of the three you cannot support with a source.
Do not guess a size band from the company's age, funding, revenue or office count, and do not infer an industry from the person's job title alone.
These three facts describe the company, not any one address: report them once, not per email.
They are not part of the email confidence score and must never raise or lower it.
CONFIDENCE SCALE
95-100
Directly published by an official or highly authoritative source, ideally independently corroborated.
85-94
Very strong evidence from multiple independent sources, with exact-address and company-pattern evidence aligned.
70-84
Strong pattern evidence plus partial or masked independent confirmation.
50-69
Pattern-derived with meaningful but incomplete corroboration.
Below 50
Speculative. Do not include unless no better result exists.
FINAL OUTPUT
Research as deeply as necessary, but return ONLY concise valid JSON.
Maximum 3 email addresses.
Maximum 4 strongest sources per email.
Always include the "company" object, even when all three of its values are null.
Do not output methodology, search queries, explanations, company history, or lengthy reasoning.
Use exactly this structure:
{
"company": {
"website": "https://company.com",
"industry": "Commercial Real Estate",
"size": "51-200"
},
"results": [
{
"email": "name@company.com",
"type": "direct",
"confidence": 95,
"status": "verified",
"sources": [
{
"name": "Official Company",
"url": "https://..."
}
]
}
]
}
Allowed type values:
"direct"
"general_company"
Allowed status values:
"verified"
"highly_likely"
"likely"
"speculative"
IMPORTANT:
Search thoroughly before answering.
Do not stop at the first result.
Prioritise source quality over source quantity.
Prefer direct evidence over email-pattern inference.
Search the exact final candidate email in quotation marks.
Include only the strongest plausible results.
Exclude speculative emails below 50 confidence unless no stronger result exists.
If no direct email reaches 70 confidence, return the best available result honestly rather than forcing certainty.
General company inboxes may be included as fallback results but must use "type": "general_company".
Fill "company" with the website, industry and size you found, and null for anything you could not source. Never drop the object itself.
A company detail you cannot find is never a reason to withhold an email address, and an email address you cannot find is never a reason to withhold the company details.
Keep the final JSON extremely concise.
""";

        /// <summary>
        /// Fills the placeholders in the static instruction with the contact
        /// details. Any field the caller left empty becomes
        /// <see cref="MissingValue"/>, so a request carrying only a name and a
        /// domain still produces a usable prompt.
        /// </summary>
        public static string Build(
            string? fullName,
            string? jobTitle,
            string? company,
            string? location,
            string? profileUrl,
            string? companyUrl)
        {
            var prompt = DemoInstruction;

            prompt = prompt.Replace("{full_name}", Value(fullName));

            foreach (var token in JobTitleTokens)
            {
                prompt = prompt.Replace(token, Value(jobTitle));
            }

            // "{company_url}" survives the "{company}" pass because the literal
            // token differs — no ordering trap here.
            prompt = prompt.Replace("{company}", Value(company));
            prompt = prompt.Replace("{location}", Value(location));
            prompt = prompt.Replace("{profile_url}", Value(profileUrl));
            prompt = prompt.Replace("{company_url}", Value(companyUrl));

            return prompt;
        }

        private static string Value(string? value) =>
            string.IsNullOrWhiteSpace(value) ? MissingValue : value.Trim();
    }
}
