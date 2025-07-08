using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PitchGenApi.Model
{
    public class ModelRate
    {
        public int Id { get; set; }
        public string ModelName { get; set; }
        public decimal InputPrice { get; set; }
        public decimal OutputPrice { get; set; }
        public decimal Temperature { get; set; }
        public int  MaxTokens { get; set; }
    } 
    
    public class zohoViewIddetails
    {
        public int Id { get; set; }
        public string zohoviewId { get; set; }
        public string zohoviewName { get; set; }
        public int clientId { get; set; }
        public int TotalContact { get; set; }
    

    }

    public class ZohoViewCreateModel
    {
        [Required(ErrorMessage = "Zoho View ID is required")]
        public string zohoviewId { get; set; }

        [Required(ErrorMessage = "Zoho View Name is required")]
        public string zohoviewName { get; set; }

        [Required(ErrorMessage = "Client ID is required")]
        public int clientId { get; set; }

        [Required(ErrorMessage = "Total Contact is required")]

        public int TotalContact { get; set; }

     



    }

    public class SettingspgViewIddetails
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)] // Absolutely essential for auto-increment
        public int SettingId { get; set; }
        public int ClientId { get; set; }
        public string Model_name { get; set; }
        public int Search_URL_count { get; set; }  // ✅ Should be int (as per DB)
        public string Search_term { get; set; }   // ✅ Should be string (as per DB)
        public string Instruction { get; set; }
        public string System_instruction { get; set; }  // ✅ Should be string (as per DB)
        public string Subject_instruction { get; set; } 

    }

}


