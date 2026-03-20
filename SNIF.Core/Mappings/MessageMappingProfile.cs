using AutoMapper;
using SNIF.Core.DTOs;
using SNIF.Core.Entities;

namespace SNIF.Core.Mappings
{
    public class MessageMappingProfile : Profile
    {
        public MessageMappingProfile()
        {
            CreateMap<Message, MessageDto>()
                .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.Id))
                .ForMember(dest => dest.Content, opt => opt.MapFrom(src => src.Content))
                .ForMember(dest => dest.SenderId, opt => opt.MapFrom(src => src.SenderId))
                .ForMember(dest => dest.ReceiverId, opt => opt.MapFrom(src => src.ReceiverId))
                .ForMember(dest => dest.MatchId, opt => opt.MapFrom(src => src.MatchId))
                .ForMember(dest => dest.IsRead, opt => opt.MapFrom(src => src.IsRead))
                .ForMember(dest => dest.Status, opt => opt.MapFrom(src => src.IsRead ? "read" : "sent"))
                .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => src.CreatedAt))
                .ForMember(dest => dest.AttachmentUrl, opt => opt.MapFrom(src => src.AttachmentUrl))
                .ForMember(dest => dest.AttachmentType, opt => opt.MapFrom(src => src.AttachmentType))
                .ForMember(dest => dest.AttachmentFileName, opt => opt.MapFrom(src => src.AttachmentFileName))
                .ForMember(dest => dest.AttachmentSizeBytes, opt => opt.MapFrom(src => src.AttachmentSizeBytes))
                .ForMember(dest => dest.Reactions, opt => opt.MapFrom(src => src.Reactions));

            CreateMap<CreateMessageDto, Message>()
                .ForMember(dest => dest.Id, opt => opt.MapFrom(src => Guid.NewGuid().ToString()))
                .ForMember(dest => dest.Content, opt => opt.MapFrom(src => src.Content))
                .ForMember(dest => dest.ReceiverId, opt => opt.MapFrom(src => src.ReceiverId))
                .ForMember(dest => dest.MatchId, opt => opt.MapFrom(src => src.MatchId))
                .ForMember(dest => dest.IsRead, opt => opt.MapFrom(src => false))
                .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => DateTime.UtcNow))
                .ForMember(dest => dest.Sender, opt => opt.Ignore())
                .ForMember(dest => dest.Receiver, opt => opt.Ignore())
                .ForMember(dest => dest.Match, opt => opt.Ignore())
                .ForMember(dest => dest.AttachmentUrl, opt => opt.Ignore())
                .ForMember(dest => dest.AttachmentType, opt => opt.Ignore())
                .ForMember(dest => dest.AttachmentFileName, opt => opt.Ignore())
                .ForMember(dest => dest.AttachmentSizeBytes, opt => opt.Ignore())
                .ForMember(dest => dest.Reactions, opt => opt.Ignore());

            CreateMap<MessageReaction, MessageReactionDto>()
                .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.Id))
                .ForMember(dest => dest.UserId, opt => opt.MapFrom(src => src.UserId))
                .ForMember(dest => dest.Emoji, opt => opt.MapFrom(src => src.Emoji))
                .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => src.CreatedAt));
        }
    }
}