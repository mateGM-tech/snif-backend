// SNIF.API/Controllers/PetController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.RateLimiting;
using SNIF.API.Attributes;
using SNIF.API.Extensions;
using SNIF.Core.DTOs;
using SNIF.Core.Enums;
using SNIF.Core.Interfaces;
using System.Security.Claims;
using MediaType = SNIF.Core.DTOs.MediaType;

namespace SNIF.API.Controllers
{
    [ApiController]
    [Route("api/pets")]
    [EnableRateLimiting("global")]
    [Authorize]
    public class PetController : ControllerBase
    {
        private readonly IPetService _petService;
        private readonly IWebHostEnvironment _environment;

        public PetController(IPetService petService, IWebHostEnvironment environment)
        {
            _petService = petService;
            _environment = environment;
        }

        [HttpGet]
        [ProducesResponseType(typeof(IEnumerable<PetDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<IEnumerable<PetDto>>> GetPets(
            [FromQuery] string userId,
            [FromQuery] PetPurpose? purpose,
            [FromQuery] string? species)
        {
            var authUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId != authUserId)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new ErrorResponse { Message = "You can only list your own pets" });

            var pets = await _petService.GetUserPetsAsync(userId);
            return Ok(pets);
        }

        [HttpGet("{id}")]
        [ProducesResponseType(typeof(PetDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(PublicPetDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetPet(string id)
        {
            try
            {
                var pet = await _petService.GetPetByIdAsync(id);
                var authUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                if (pet.OwnerId == authUserId)
                    return Ok(pet);

                // Return reduced DTO for non-owners
                var publicPet = new PublicPetDto
                {
                    Id = pet.Id,
                    Name = pet.Name,
                    Species = pet.Species,
                    Breed = pet.Breed,
                    Age = pet.Age,
                    Gender = pet.Gender,
                    Purpose = pet.Purpose,
                    Personality = pet.Personality,
                    City = pet.Location?.City,
                    Media = pet.Media.Select(m => new PublicMediaResponseDto
                    {
                        Id = m.Id,
                        Url = m.Url,
                        Type = m.Type,
                        Title = m.Title,
                        Description = m.Description,
                        CreatedAt = m.CreatedAt
                    }).ToList()
                };
                return Ok(publicPet);
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new ErrorResponse { Message = "Pet not found" });
            }
        }

        [HttpPost]
        [Authorize]
        [EnforceUsageLimit(UsageType.PetCreation)]
        [ProducesResponseType(typeof(PetDto), StatusCodes.Status201Created)]
        public async Task<ActionResult<PetDto>> CreatePet([FromBody] CreatePetDto createPetDto)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? throw new UnauthorizedAccessException();

            var pet = await _petService.CreatePetAsync(userId, createPetDto);
            return CreatedAtAction(nameof(GetPet), new { id = pet.Id }, pet);
        }


        [Authorize]
        [HttpPut("{id}")]
        [ProducesResponseType(typeof(PetDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<PetDto>> UpdatePet(string id, [FromBody] UpdatePetDto updatePetDto)
        {
            if (string.IsNullOrEmpty(id))
                return BadRequest(new ErrorResponse { Message = "Pet ID is required" });

            if (!ModelState.IsValid)
                return BadRequest(new ErrorResponse { Message = "Invalid update data" });

            var authUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            try
            {
                var existingPet = await _petService.GetPetByIdAsync(id);
                if (existingPet.OwnerId != authUserId)
                    return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse { Message = "You can only modify your own pets" });

                var pet = await _petService.UpdatePetAsync(id, updatePetDto);
                return Ok(pet);
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new ErrorResponse { Message = "Pet not found" });
            }
        }

        [Authorize]
        [HttpDelete("{id}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> DeletePet(string id)
        {
            if (string.IsNullOrEmpty(id))
                return BadRequest(new ErrorResponse { Message = "Pet ID is required" });

            // Security Fix: Verify ownership before allowing delete
            var authUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            try
            {
                var existingPet = await _petService.GetPetByIdAsync(id);
                if (existingPet.OwnerId != authUserId)
                    return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse { Message = "You can only delete your own pets" });

                await _petService.DeletePetAsync(id);
                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new ErrorResponse { Message = "Pet not found" });
            }
        }


        [HttpPost("{id}/media")]
        [Authorize]
        [RequestSizeLimit(10_485_760)]
        [ProducesResponseType(typeof(MediaResponseDto), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<MediaResponseDto>> AddMedia(
            string id,
            [FromBody] AddMediaDto mediaDto)
        {
            var authUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            try
            {
                var existingPet = await _petService.GetPetByIdAsync(id);
                if (existingPet.OwnerId != authUserId)
                    return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse { Message = "You can only add media to your own pets" });

                var baseUrl = $"{Request.Scheme}://{Request.Host}";
                var result = await _petService.AddPetMediaAsync(id, mediaDto, baseUrl);
                return CreatedAtAction(
                    nameof(GetMediaInfo),
                    new { id, mediaId = result.Id },
                    result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new ErrorResponse { Message = ex.Message });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new ErrorResponse { Message = "Pet not found" });
            }
        }

        [HttpGet("{id}/media")]
        [ProducesResponseType(typeof(IEnumerable<MediaResponseDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(IEnumerable<PublicMediaResponseDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetMedia(
            string id,
            [FromQuery] MediaType? type)
        {
            try
            {
                var baseUrl = $"{Request.Scheme}://{Request.Host}";
                var media = await _petService.GetPetMediaAsync(id, type, baseUrl);

                var authUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                var pet = await _petService.GetPetByIdAsync(id);

                if (pet.OwnerId == authUserId)
                    return Ok(media);

                // Non-owners see public media without internal storage metadata
                var publicMedia = media.Select(m => new PublicMediaResponseDto
                {
                    Id = m.Id,
                    Url = m.Url,
                    Type = m.Type,
                    Title = m.Title,
                    Description = m.Description,
                    CreatedAt = m.CreatedAt
                });
                return Ok(publicMedia);
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new ErrorResponse { Message = "Pet not found" });
            }
        }

        [HttpGet("media/{mediaId}")]
        [ProducesResponseType(typeof(MediaResponseDto), StatusCodes.Status200OK)]
        public async Task<ActionResult<MediaResponseDto>> GetMediaInfo(string mediaId)
        {
            try
            {
                var baseUrl = $"{Request.Scheme}://{Request.Host}";
                var media = await _petService.GetMediaByIdAsync(mediaId, baseUrl);
                return Ok(media);
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new ErrorResponse { Message = "Media not found" });
            }
        }

        [HttpDelete("{id}/media/{mediaId}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> DeleteMedia(string id, string mediaId)
        {
            var authUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            try
            {
                var existingPet = await _petService.GetPetByIdAsync(id);
                if (existingPet.OwnerId != authUserId)
                    return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse { Message = "You can only delete media from your own pets" });

                await _petService.DeletePetMediaAsync(id, mediaId);
                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new ErrorResponse { Message = "Media not found" });
            }
        }

        [HttpGet("{petId}/discovery-preferences")]
        [Authorize]
        [ProducesResponseType(typeof(DiscoveryPreferencesDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<ActionResult<DiscoveryPreferencesDto>> GetDiscoveryPreferences(string petId)
        {
            try
            {
                var prefs = await _petService.GetDiscoveryPreferencesAsync(petId);
                return Ok(prefs);
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new ErrorResponse { Message = "Pet not found" });
            }
        }

        [HttpPut("{petId}/discovery-preferences")]
        [Authorize]
        [ProducesResponseType(typeof(DiscoveryPreferencesDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<DiscoveryPreferencesDto>> UpdateDiscoveryPreferences(
            string petId,
            [FromBody] UpdateDiscoveryPreferencesDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(new ErrorResponse { Message = "Invalid preferences data" });

            var authUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            try
            {
                var existingPet = await _petService.GetPetByIdAsync(petId);
                if (existingPet.OwnerId != authUserId)
                    return StatusCode(StatusCodes.Status403Forbidden,
                        new ErrorResponse { Message = "You can only modify your own pet's preferences" });

                var prefs = await _petService.UpdateDiscoveryPreferencesAsync(petId, dto);
                return Ok(prefs);
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new ErrorResponse { Message = "Pet not found" });
            }
        }
    }
}