
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using SNIF.Core.Enums;

namespace SNIF.Core.DTOs
{
    public record PetDto
    {
        public string Id { get; init; } = null!;
        public string Name { get; init; } = null!;
        public string Species { get; init; } = null!;
        public string Breed { get; init; } = null!;
        public int Age { get; init; }
        public Gender Gender { get; init; }
        public ICollection<PetPurpose> Purpose { get; init; } = new List<PetPurpose>();
        public ICollection<string> Personality { get; init; } = new List<string>();
        public MedicalHistoryDto? MedicalHistory { get; init; }
        public ICollection<MediaResponseDto> Media { get; init; } = new List<MediaResponseDto>();
        public LocationDto? Location { get; init; }
        public string OwnerId { get; init; } = null!;
        public DateTime CreatedAt { get; init; }
        public DateTime? UpdatedAt { get; init; }
        public bool IsLocked { get; init; }
        public string? EntitlementLockReason { get; init; }
        public IDictionary<string, string> Links { get; init; } = new Dictionary<string, string>();
    }

    public record CreatePetDto
    {
        [Required]
        [StringLength(100)]
        public string Name { get; init; } = null!;

        [Required]
        [StringLength(50)]
        public string Species { get; init; } = null!;

        [Required]
        [StringLength(100)]
        public string Breed { get; init; } = null!;

        [Range(0, 50)]
        public int Age { get; init; }

        public Gender Gender { get; init; }
        public ICollection<PetPurpose> Purpose { get; init; } = new List<PetPurpose>();
        public ICollection<string> Personality { get; init; } = new List<string>();
        public CreateMedicalHistoryDto? MedicalHistory { get; init; }
        public LocationDto? Location { get; init; }
        public ICollection<AddMediaDto>? Media { get; init; }

    }

    public record UpdatePetDto
    {
        [StringLength(100)]
        public string? Name { get; init; }

        [StringLength(50)]
        public string? Species { get; init; }

        [StringLength(100)]
        public string? Breed { get; init; }

        [Range(0, 50)]
        public int? Age { get; init; }

        public Gender? Gender { get; init; }
        public ICollection<PetPurpose>? Purpose { get; init; }
        public ICollection<string>? Personality { get; init; }
        public UpdateMedicalHistoryDto? MedicalHistory { get; init; }
        public LocationDto? Location { get; init; }
    }

    public enum MediaType
    {
        Photo,
        Video
    }

    public record MediaResponseDto
    {
        public string Id { get; init; } = null!;
        public string Url { get; init; } = null!;
        public MediaType Type { get; init; }
        public string FileName { get; init; } = null!;
        public string ContentType { get; init; } = null!;
        public long Size { get; init; }
        public string Title { get; init; } = null!;
        public string? Description { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? UpdatedAt { get; init; }

        public IDictionary<string, string> Links { get; init; } = new Dictionary<string, string>();
    }

    public record AddMediaDto
    {
        [Required]
        [Base64String]
        public string Base64Data { get; init; } = null!;

        [Required]
        [ValidMediaContentType]
        public string ContentType { get; init; } = null!;

        [Required]
        public string FileName { get; init; } = null!;

        [Required]
        public MediaType Type { get; init; }

        [StringLength(100)]
        public string? Title { get; init; }

        public string? Description { get; init; }
    }

    public record MedicalHistoryDto
    {
        public bool IsVaccinated { get; init; }
        public ICollection<string> HealthIssues { get; init; } = new List<string>();
        public ICollection<string> VaccinationRecords { get; init; } = new List<string>();
        public DateTime? LastCheckup { get; init; }
        public string? VetContact { get; init; }
    }

    public record CreateMedicalHistoryDto
    {
        public bool IsVaccinated { get; init; }
        public ICollection<string> HealthIssues { get; init; } = new List<string>();
        public ICollection<string> VaccinationRecords { get; init; } = new List<string>();
        public ICollection<AddMediaDto>? Media { get; init; }
        public DateTime? LastCheckup { get; init; }
        public string? VetContact { get; init; }
    }

    public record UpdateMedicalHistoryDto
    {
        public bool? IsVaccinated { get; init; }
        public ICollection<string>? HealthIssues { get; init; }
        public ICollection<string>? VaccinationRecords { get; init; }
        public DateTime? LastCheckup { get; init; }
        public string? VetContact { get; init; }
    }

    /// <summary>
    /// Reduced pet DTO returned to non-owners. Excludes medical records,
    /// exact location, discovery preferences, and internal metadata.
    /// </summary>
    public record PublicPetDto
    {
        public string Id { get; init; } = null!;
        public string Name { get; init; } = null!;
        public string Species { get; init; } = null!;
        public string Breed { get; init; } = null!;
        public int Age { get; init; }
        public Gender Gender { get; init; }
        public ICollection<PetPurpose> Purpose { get; init; } = new List<PetPurpose>();
        public ICollection<string> Personality { get; init; } = new List<string>();
        public string? City { get; init; }
        public ICollection<PublicMediaResponseDto> Media { get; init; } = new List<PublicMediaResponseDto>();
    }

    /// <summary>
    /// Reduced media DTO that excludes internal storage metadata (FileName, ContentType, Size).
    /// </summary>
    public record PublicMediaResponseDto
    {
        public string Id { get; init; } = null!;
        public string Url { get; init; } = null!;
        public MediaType Type { get; init; }
        public string? Title { get; init; }
        public string? Description { get; init; }
        public DateTime CreatedAt { get; init; }
    }
}