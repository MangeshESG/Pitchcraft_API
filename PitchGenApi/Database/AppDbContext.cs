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



        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ModelRate>().ToTable("ModelRates");
            modelBuilder.Entity<zohoViewIddetails>().ToTable("zohoViewIddetails");
            modelBuilder.Entity<SettingspgViewIddetails>().ToTable("Settingspg");

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

            base.OnModelCreating(modelBuilder);
        }
    }
}