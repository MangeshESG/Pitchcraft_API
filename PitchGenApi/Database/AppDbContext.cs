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

        public DbSet<CampaignTemplate> CampaignTemplates { get; set; }
        public DbSet<CampaignConversation> CampaignConversations { get; set; }

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

            // ✅ Composite primary key for SegmentContact
            modelBuilder.Entity<SegmentContact>()
                .HasKey(sc => new { sc.SegmentId, sc.ContactId });


            modelBuilder.Entity<CampaignTemplate>()
               .HasIndex(c => c.ClientId)
               .HasDatabaseName("IX_CampaignTemplate_ClientId");

            modelBuilder.Entity<CampaignTemplate>()
                .HasIndex(c => new { c.ClientId, c.TemplateName })
                .HasDatabaseName("IX_CampaignTemplate_ClientId_TemplateName");

            modelBuilder.Entity<CampaignTemplate>()
                .Property(c => c.PlaceholderValues)
                .HasColumnType("nvarchar(max)");

            // Configure relationship between CampaignTemplate and CampaignConversation
            modelBuilder.Entity<CampaignTemplate>()
                .HasOne(t => t.Conversation)
                .WithOne(c => c.CampaignTemplate)
                .HasForeignKey<CampaignConversation>(c => c.CampaignTemplateId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<CampaignConversation>()
                .HasIndex(c => c.ClientId)
                .HasDatabaseName("IX_CampaignConversation_ClientId");

            modelBuilder.Entity<CampaignConversation>()
                .Property(c => c.ConversationData)
                .HasColumnType("nvarchar(max)");


            base.OnModelCreating(modelBuilder);
        }

        internal async Task FirstOrDefaultAsync()
        {
            throw new NotImplementedException();
        }

        internal async Task GetAllModelInfoAsync()
        {
            throw new NotImplementedException();
        }

        internal async Task GetClientId()
        {
            throw new NotImplementedException();
        }
    }
}
