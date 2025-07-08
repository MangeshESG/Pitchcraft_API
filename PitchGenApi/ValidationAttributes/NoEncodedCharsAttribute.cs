using System;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace PitchGenApi.ValidationAttributes
{
    public class NoEncodedCharsAttribute : ValidationAttribute
    {
        protected override ValidationResult IsValid(object value, ValidationContext validationContext)
        {
            var str = value as string;
            if (!string.IsNullOrEmpty(str) && str.Contains("%") && Regex.IsMatch(str, @"%[0-9a-fA-F]{2}"))
            {
                return new ValidationResult("Encoded characters are not allowed.");
            }
            return ValidationResult.Success;
        }
    }
}
