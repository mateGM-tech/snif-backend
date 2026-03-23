using AutoMapper;
using SNIF.Core.DTOs;
using SNIF.Core.Entities;
using SNIF.Core.Utilities;

namespace SNIF.Core.Mappings
{
    public class UserMappingProfile : Profile
    {
        public UserMappingProfile()
        {
            CreateMap<User, UserDto>()
                .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.Id))
                .ForMember(dest => dest.Email, opt => opt.MapFrom(src => src.Email ?? string.Empty))
                .ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.Name ?? string.Empty))
                .ForMember(dest => dest.Location, opt => opt.MapFrom(src => src.Location))
                .ForMember(dest => dest.Pets, opt => opt.MapFrom(src => src.Pets ?? new List<Pet>()))
                .ForMember(dest => dest.Preferences, opt => opt.MapFrom(src => src.Preferences))
                .ForMember(dest => dest.ProfilePicturePath, opt => opt.MapFrom(src =>
                    MediaPathResolver.ResolveProfilePicturePath(src.ProfilePicturePath)))
                .ForMember(dest => dest.HasGoogleLinked, opt => opt.MapFrom(src => src.GoogleSubjectId != null))
                .ForMember(dest => dest.HasPassword, opt => opt.MapFrom(src => src.PasswordHash != null))
                .ForMember(dest => dest.EmailConfirmed, opt => opt.MapFrom(src => src.EmailConfirmed))
                .PreserveReferences();  // Handle circular references

            CreateMap<User, AuthResponseDto>()
                .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.Id))
                .ForMember(dest => dest.Email, opt => opt.MapFrom(src => src.Email ?? string.Empty))
                .ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.Name ?? string.Empty))
                .ForMember(dest => dest.Role, opt => opt.Ignore())
                .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => src.CreatedAt))
                .ForMember(dest => dest.EmailConfirmed, opt => opt.MapFrom(src => src.EmailConfirmed))
                .ForMember(dest => dest.Token, opt => opt.Ignore())
                .ForMember(dest => dest.AuthStatus, opt => opt.Ignore())
                .ForMember(dest => dest.RequiresEmailConfirmation, opt => opt.Ignore())
                .ForMember(dest => dest.CanResendConfirmation, opt => opt.Ignore())
                .ForMember(dest => dest.Message, opt => opt.Ignore());
        }
    }
}