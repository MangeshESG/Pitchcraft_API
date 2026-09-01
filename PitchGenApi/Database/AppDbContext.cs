using Microsoft.EntityFrameworkCore;
using PitchGenApi.Model;
using PitchGenApi.Models;

namespace PitchGenApi.Database
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<tbl_clientdetails> tbl_clientdetails { get; set; }
        public DbSet<Prompt> Prompts { get; set; }
        public DbSet<PitchGendata> PitchGendata { get; set; }
        public DbSet<ModelRate> ModelRates { get; set; }
        public DbSet<zohoViewIddetails> zohoViewIddetails { get; set; }
        public DbSet<zohoViewIddetails> clientId { get; set; }
        public DbSet<SettingspgViewIddetails> Settingspg { get; set; }
        public DbSet<SettingspgViewIddetails> ClientId { get; set; }
        public DbSet<SettingspgViewIddetails> SettingspgViewIddetails { get; set; }
        public DbSet<UserDateTimeSettings> UserDateTimeSettings { get; set; }
        public DbSet<EmailTrackingLog> EmailTrackingLogs { get; set; }
        public DbSet<SequenceStep> SequenceSteps { get; set; }
        public DbSet<SmtpCredentials> SmtpCredentials { get; set; }
        public DbSet<EmailLog> EmailLogs { get; set; }
        public DbSet<BccEmail> BccEmail { get; set; }
        public DbSet<Campaign> Campaigns { get; set; }
        public DbSet<DataFile> data_files { get; set; }
        public DbSet<Contact> contacts { get; set; }
        public DbSet<Segment> segments { get; set; }
        public DbSet<SegmentContact> segmentContacts { get; set; }
        public DbSet<ToneSettings> ToneSettings { get; set; }

        // ✅ Campaign Template System
        public DbSet<CampaignTemplateDefinition> CampaignTemplateDefinitions { get; set; }
        public DbSet<CampaignTemplate> CampaignTemplates { get; set; }
        public DbSet<CampaignConversation> CampaignConversations { get; set; }

        // ✅ Newly added DbSets (for LoginController, registration, Zoho, etc.)
        public DbSet<ClientDetails> ClientDetails { get; set; }
        public DbSet<UserCredits> UserCredits { get; set; }
        public DbSet<EmailOtpVerification> EmailOtpVerifications { get; set; }
        public DbSet<TempRegisterData> TempRegisterData { get; set; }
        public DbSet<Countriesdropdown> Countriesdropdown { get; set; }
        public DbSet<StripeSubscription> StripeSubscription { get; set; }
        public DbSet<EmailTemplates> EmailTemplates { get; set; }
        public DbSet<FinalUserCredit> FinalUserCredit { get; set; }
        public DbSet<UnsubscribedContacts> UnsubscribedContacts { get; set; }
        public DbSet<PlaceholderDefinition> PlaceholderDefinitions { get; set; }
        public DbSet<DomainVerification> DomainVerification { get; set; }
        public DbSet<DomainEmailVerification> DomainEmailVerification { get; set; }
        public DbSet<Notes> Notes { get; set; }
        public DbSet<UploadedImage> UploadedImages { get; set; }
        public DbSet<ContactAttachments> ContactAttachments { get; set; }
        public DbSet<CrmCustomField> crm_custom_fields { get; set; }
        public DbSet<ContactCustomFieldValue> contact_custom_field_values { get; set; }
        public DbSet<CrmColumnPreference> crm_column_preferences { get; set; }

        public DbSet<CrmView> crm_views { get; set; }

        public DbSet<CrmViewDatafile> crm_view_datafiles { get; set; }

        public DbSet<CrmViewSegment> crm_view_segments { get; set; }

        public DbSet<CrmViewExcludedDatafile> crm_view_excluded_datafiles { get; set; }
        public DbSet<Inboxcredentials> Inboxcredentials { get; set; }
        public DbSet<EmailReplies> EmailReplies { get; set; }
        public DbSet<EmailOAuthTokens> EmailOAuthTokens { get; set; }
        public DbSet<InboxEmails> InboxEmails { get; set; }
        public DbSet<EmailAttachment> EmailAttachments { get; set; }
        public DbSet<EmailSignatures> EmailSignatures { get; set; }
        public DbSet<PinnedEmails> PinnedEmails { get; set; }
        public DbSet<KraftHistory> KraftHistory { get; set; }
        public DbSet<Domain> Domain { get; set; }
        public DbSet<EmailBounce> EmailBounces { get; set; }
        public DbSet<UnlockedContacts> UnlockedContacts { get; set; }
        public DbSet<EmailPattern> EmailPattern { get; set; }
        // ✅ Admin-controlled model per AI purpose (Settings > AI models)
        public DbSet<AiModelSetting> ai_model_settings { get; set; }
        // ✅ Admin-controlled security switches (Settings > Security)
        public DbSet<SecuritySetting> app_security_settings { get; set; }
        // ✅ Admin-editable AI instructions (Settings > Admin > Prompts)
        public DbSet<PromptSetting> app_prompt_settings { get; set; }
        // ✅ LinkedIn messages krafted per contact + the "Sent" checkbox state
        public DbSet<LinkedInMessage> LinkedInMessages { get; set; }
        public DbSet<OutgoingMailboxGroup> OutgoingMailboxGroups { get; set; }
        public DbSet<OutgoingMailboxGroupMember> OutgoingMailboxGroupMembers { get; set; }

        // ✅ Audience Assurance — the four validation checks run over contacts
        public DbSet<ContactFitBrief> contact_fit_briefs { get; set; }
        public DbSet<ContactValidation> contact_validations { get; set; }
        public DbSet<ContactValidationJob> contact_validation_jobs { get; set; }
        public DbSet<ContactValidationJobItem> contact_validation_job_items { get; set; }
        public DbSet<CompanyIntelligence> company_intelligence { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ModelRate>().ToTable("ModelRates");

            modelBuilder.Entity<AiModelSetting>(entity =>
            {
                entity.ToTable("ai_model_settings");
                entity.HasKey(e => e.id);
                entity.Property(e => e.purpose_key).IsRequired().HasMaxLength(100);
                entity.Property(e => e.model_name).IsRequired().HasMaxLength(200);
                entity.Property(e => e.updated_by).HasMaxLength(200);
                entity.Property(e => e.updated_at).HasDefaultValueSql("GETUTCDATE()");

                // One row per purpose — the settings are application-wide.
                entity.HasIndex(e => e.purpose_key).IsUnique();
            });
            modelBuilder.Entity<PromptSetting>(entity =>
            {
                entity.ToTable("app_prompt_settings");
                entity.HasKey(e => e.id);
                entity.Property(e => e.prompt_key).IsRequired().HasMaxLength(100);
                // The instruction runs to thousands of characters — nvarchar(max).
                entity.Property(e => e.prompt_text).IsRequired();
                entity.Property(e => e.updated_by).HasMaxLength(200);
                entity.Property(e => e.updated_at).HasDefaultValueSql("GETUTCDATE()");

                // One row per prompt — the instructions are application-wide.
                entity.HasIndex(e => e.prompt_key).IsUnique();
            });
            modelBuilder.Entity<SecuritySetting>(entity =>
            {
                entity.ToTable("app_security_settings");
                entity.HasKey(e => e.id);
                entity.Property(e => e.setting_key).IsRequired().HasMaxLength(100);
                entity.Property(e => e.setting_value).IsRequired().HasMaxLength(200);
                entity.Property(e => e.updated_by).HasMaxLength(200);
                entity.Property(e => e.updated_at).HasDefaultValueSql("GETUTCDATE()");

                // One row per switch — the settings are application-wide.
                entity.HasIndex(e => e.setting_key).IsUnique();
            });

            modelBuilder.Entity<zohoViewIddetails>().ToTable("zohoViewIddetails");
            modelBuilder.Entity<SettingspgViewIddetails>().ToTable("Settingspg");
            modelBuilder.Entity<UserDateTimeSettings>(entity =>
            {
                entity.ToTable("UserDateTimeSettings");
                entity.HasIndex(x => x.ClientId).IsUnique();
            });

            modelBuilder.Entity<CrmViewExcludedDatafile>()
                 .HasKey(x => new { x.view_id, x.datafile_id });

            // ✅ Client-level list-view column layout (show/hide + sequence)
            modelBuilder.Entity<CrmColumnPreference>(entity =>
            {
                entity.ToTable("crm_column_preferences");
                entity.HasKey(e => e.id);
                entity.Property(e => e.column_key).IsRequired().HasMaxLength(200);
                entity.Property(e => e.label).HasMaxLength(300);
                entity.Property(e => e.is_visible).HasDefaultValue(true);
                entity.Property(e => e.created_at).HasDefaultValueSql("GETUTCDATE()");

                // One row per column per client — the layout is shared across all list views.
                entity.HasIndex(e => new { e.client_id, e.column_key }).IsUnique();
                entity.HasIndex(e => new { e.client_id, e.sort_order });
            });

            modelBuilder.Entity<Campaign>()
                .HasIndex(c => c.ClientId);

            modelBuilder.Entity<Campaign>()
                .HasIndex(c => c.PromptId);

            modelBuilder.Entity<Contact>()
                .HasIndex(c => new { c.DataFileId, c.email })
                .IsUnique();

            // Composite primary key for SegmentContact
            modelBuilder.Entity<SegmentContact>()
                .HasKey(sc => new { sc.SegmentId, sc.ContactId });

            // ✅ CampaignTemplateDefinition Configuration
            modelBuilder.Entity<CampaignTemplateDefinition>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.TemplateName).IsUnique();
                entity.Property(e => e.TemplateName).IsRequired().HasMaxLength(255);
                entity.Property(e => e.IsActive).HasDefaultValue(true);
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
            });

            // ✅ CampaignTemplate Configuration
            modelBuilder.Entity<CampaignTemplate>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.ClientId);
                entity.HasIndex(e => e.TemplateDefinitionId);
                entity.HasIndex(e => new { e.ClientId, e.TemplateDefinitionId });

                entity.Property(e => e.PlaceholderValues).HasColumnType("nvarchar(max)");
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

                // Relationship with TemplateDefinition
                entity.HasOne(e => e.TemplateDefinition)
                      .WithMany(d => d.CampaignTemplates)
                      .HasForeignKey(e => e.TemplateDefinitionId)
                      .OnDelete(DeleteBehavior.Cascade);

                // Relationship with Conversation
                entity.HasOne(e => e.Conversation)
                      .WithOne(c => c.CampaignTemplate)
                      .HasForeignKey<CampaignConversation>(c => c.CampaignTemplateId)
                      .OnDelete(DeleteBehavior.Cascade);
            });


        

            // ✅ CampaignConversation Configuration
            modelBuilder.Entity<CampaignConversation>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.ClientId);
                entity.Property(e => e.ConversationData).HasColumnType("nvarchar(max)");
            });

            // ✅ LinkedIn messages. Mirrors the table created in SQL: the
            // clustered index is (client_id, contact_id, id DESC) — the shape
            // every read uses — while the identity stays a nonclustered PK.
            modelBuilder.Entity<LinkedInMessage>(entity =>
            {
                entity.ToTable("linkedin_messages");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Body).HasColumnType("nvarchar(max)");
                entity.Property(e => e.BodyHash).HasColumnType("binary(32)");
                entity.HasIndex(e => new { e.ClientId, e.ContactId, e.Id });
                entity.HasIndex(e => new { e.ClientId, e.MsgUid }).IsUnique();
                entity.HasIndex(e => new { e.ClientId, e.SentAt });

                // Mirrors UX_linkedin_messages_inbound_dedupe: filtered to pasted
                // inbound rows, so re-pasting the same reply collides instead of
                // storing a duplicate.
                entity.HasIndex(e => new { e.ClientId, e.ContactId, e.BodyHash })
                      .IsUnique()
                      .HasFilter("[direction] = 'inbound' AND [body_hash] IS NOT NULL");
            });

            // ✅ Audience Assurance. The tables were created by hand in SQL, as
            // the rest of the schema since InitialCreate was — so this
            // configuration is the only description of their shape in the
            // codebase. Keep it in step with the database by hand.
            modelBuilder.Entity<ContactFitBrief>(entity =>
            {
                entity.ToTable("contact_fit_briefs");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => new { e.ClientId, e.Name }).IsUnique();

                // At most one default per client, enforced in the database so a
                // concurrent "set default" cannot leave two.
                entity.HasIndex(e => e.ClientId)
                      .IsUnique()
                      .HasFilter("[is_default] = 1");
            });

            modelBuilder.Entity<ContactValidation>(entity =>
            {
                entity.ToTable("contact_validations");
                entity.HasKey(e => e.Id);

                // One row per contact — every write is an upsert on this key.
                entity.HasIndex(e => new { e.ClientId, e.ContactId }).IsUnique();
            });

            modelBuilder.Entity<ContactValidationJob>(entity =>
            {
                entity.ToTable("contact_validation_jobs");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.CalculatedCost).HasColumnType("decimal(18,6)");

                // The cost log reads newest-first per client.
                entity.HasIndex(e => new { e.ClientId, e.CreatedAt });

                // The background runner claims work by status.
                entity.HasIndex(e => e.Status);
            });

            modelBuilder.Entity<ContactValidationJobItem>(entity =>
            {
                entity.ToTable("contact_validation_job_items");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => new { e.JobId, e.ContactId }).IsUnique();
            });

            modelBuilder.Entity<CompanyIntelligence>(entity =>
            {
                entity.ToTable("company_intelligence");
                entity.HasKey(e => e.Id);

                // Domain is the preferred lookup key; the filter keeps rows that
                // only have a company name out of the unique constraint.
                entity.HasIndex(e => new { e.ClientId, e.Domain })
                      .IsUnique()
                      .HasFilter("[domain] IS NOT NULL");

                entity.HasIndex(e => new { e.ClientId, e.CompanyNameNormalised });
            });

            base.OnModelCreating(modelBuilder);
        }
    }
}
