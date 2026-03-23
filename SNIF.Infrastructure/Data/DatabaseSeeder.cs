using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SNIF.Core.Constants;
using SNIF.Core.DTOs;
using SNIF.Core.Entities;
using SNIF.Core.Enums;
using SNIF.Core.Models;

namespace SNIF.Infrastructure.Data;

public static class DatabaseSeeder
{
    private static readonly Random _random = new(42); // Fixed seed for reproducibility
    private static readonly SeededIdentityUser[] PrivilegedUsers =
    {
        new("admin@snif.hu", "SNIF Admin", AppRoles.SuperAdmin, AppRoles.Admin),
        new("support@snif.hu", "SNIF Support", AppRoles.Support)
    };

    public static async Task SeedAsync(IServiceProvider serviceProvider)
    {
        var context = serviceProvider.GetRequiredService<SNIFContext>();
        var userManager = serviceProvider.GetRequiredService<UserManager<User>>();
        var roleManager = serviceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();

        // Seed roles (always idempotent)
        await SeedRolesAsync(roleManager);

        var privilegedSeedOptions = GetPrivilegedSeedOptions(configuration);

        if (privilegedSeedOptions.Enabled)
        {
            await SeedPrivilegedUsersAsync(userManager, privilegedSeedOptions.Password);
        }

        // Seed Animal Breeds
        if (!context.AnimalBreeds.Any())
        {
            var breedsToSeed = SeedAnimalBreeds();
            context.AnimalBreeds.AddRange(breedsToSeed);
            await context.SaveChangesAsync();
        }

        // Idempotency check
        if (context.Users.Count() > PrivilegedUsers.Length)
            return;

        // 1. Create users
        var users = await SeedUsersAsync(userManager);

        // 2. Create pets
        var pets = SeedPets(context, users);
        context.Pets.AddRange(pets);
        await context.SaveChangesAsync();

        // 3. Create PetMedia records for seeded photos
        var petMedia = SeedPetMedia(pets);
        context.PetMedia.AddRange(petMedia);
        await context.SaveChangesAsync();

        // 4. Create matches
        var matches = SeedMatches(context, pets);
        context.Matches.AddRange(matches);
        await context.SaveChangesAsync();

        // 5. Create messages for accepted matches
        var messages = SeedMessages(matches, users, pets);
        context.Messages.AddRange(messages);
        await context.SaveChangesAsync();
    }

    private static async Task SeedRolesAsync(RoleManager<IdentityRole> roleManager)
    {
        foreach (var role in AppRoles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }
    }

    private static PrivilegedSeedOptions GetPrivilegedSeedOptions(IConfiguration configuration)
    {
        var enabled = configuration.GetValue<bool>("SeedUsers:PrivilegedUsers:Enabled");

        if (!enabled)
        {
            return PrivilegedSeedOptions.Disabled;
        }

        var password = configuration["SeedUsers:PrivilegedUsers:InitialPassword"];
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "Privileged user seeding is enabled but SeedUsers:PrivilegedUsers:InitialPassword is not configured.");
        }

        return new PrivilegedSeedOptions(true, password);
    }

    private static async Task SeedPrivilegedUsersAsync(UserManager<User> userManager, string privilegedUserPassword)
    {
        foreach (var seededUser in PrivilegedUsers)
        {
            await SeedPrivilegedUserAsync(userManager, seededUser, privilegedUserPassword);
        }
    }

    private static async Task SeedPrivilegedUserAsync(
        UserManager<User> userManager,
        SeededIdentityUser seededUser,
        string privilegedUserPassword)
    {
        var existingUser = await userManager.FindByEmailAsync(seededUser.Email);

        if (existingUser == null)
        {
            var newUser = new User
            {
                UserName = seededUser.Email,
                Email = seededUser.Email,
                EmailConfirmed = true,
                Name = seededUser.DisplayName,
                CreatedAt = DateTime.UtcNow,
            };

            var createResult = await userManager.CreateAsync(newUser, privilegedUserPassword);
            EnsureSuccess(createResult, $"Failed to create seeded user '{seededUser.Email}'.");
            existingUser = newUser;
        }
        else
        {
            var requiresUpdate = false;

            if (!existingUser.EmailConfirmed)
            {
                existingUser.EmailConfirmed = true;
                requiresUpdate = true;
            }

            if (string.IsNullOrWhiteSpace(existingUser.UserName))
            {
                existingUser.UserName = seededUser.Email;
                requiresUpdate = true;
            }

            if (string.IsNullOrWhiteSpace(existingUser.Name))
            {
                existingUser.Name = seededUser.DisplayName;
                requiresUpdate = true;
            }

            if (requiresUpdate)
            {
                var updateResult = await userManager.UpdateAsync(existingUser);
                EnsureSuccess(updateResult, $"Failed to update seeded user '{seededUser.Email}'.");
            }

            if (string.IsNullOrWhiteSpace(existingUser.PasswordHash))
            {
                var addPasswordResult = await userManager.AddPasswordAsync(existingUser, privilegedUserPassword);
                EnsureSuccess(addPasswordResult, $"Failed to assign a password to seeded user '{seededUser.Email}'.");
            }
        }

        foreach (var role in seededUser.Roles)
        {
            if (await userManager.IsInRoleAsync(existingUser, role))
            {
                continue;
            }

            var addToRoleResult = await userManager.AddToRoleAsync(existingUser, role);
            EnsureSuccess(addToRoleResult, $"Failed to assign role '{role}' to seeded user '{seededUser.Email}'.");
        }
    }

    private static void EnsureSuccess(IdentityResult result, string message)
    {
        if (result.Succeeded)
        {
            return;
        }

        var errors = string.Join(", ", result.Errors.Select(error => error.Description));
        throw new InvalidOperationException($"{message} Errors: {errors}");
    }

    private sealed record SeededIdentityUser(string Email, string DisplayName, params string[] Roles);
    private sealed record PrivilegedSeedOptions(bool Enabled, string Password)
    {
        public static PrivilegedSeedOptions Disabled { get; } = new(false, string.Empty);
    }

    private static async Task<List<User>> SeedUsersAsync(UserManager<User> userManager)
    {
        var users = new List<User>();
        var now = DateTime.UtcNow;

        for (int i = 0; i < 100; i++)
        {
            var (firstName, lastName) = GetName(i);
            var city = Cities[i % Cities.Length];
            var isOnline = i < 20; // ~20% online

            var user = new User
            {
                UserName = $"{firstName.ToLower()}.{lastName.ToLower()}@snif.app",
                Email = $"{firstName.ToLower()}.{lastName.ToLower()}@snif.app",
                EmailConfirmed = true,
                Name = $"{firstName} {lastName}",
                Location = new Location
                {
                    Latitude = city.Lat + (_random.NextDouble() - 0.5) * 0.05,
                    Longitude = city.Lng + (_random.NextDouble() - 0.5) * 0.05,
                    City = city.Name,
                    Country = city.Country,
                    Address = $"Street {_random.Next(1, 200)} Nr. {_random.Next(1, 50)}",
                    CreatedAt = now
                },
                Preferences = new UserPreferences
                {
                    SearchRadius = 20 + _random.Next(0, 80),
                    ShowOnlineStatus = _random.NextDouble() > 0.2,
                    CreatedAt = now,
                    NotificationSettings = new NotificationSettings
                    {
                        EmailNotifications = true,
                        PushNotifications = true,
                        NewMatchNotifications = true,
                        MessageNotifications = true,
                        BreedingRequestNotifications = _random.NextDouble() > 0.3,
                        PlaydateRequestNotifications = true,
                        CreatedAt = now
                    }
                },
                ProfilePicturePath = $"https://picsum.photos/seed/user-{i}/200/200",
                IsOnline = isOnline,
                LastSeen = isOnline ? now : now.AddMinutes(-_random.Next(1, 43200)), // up to 30 days
                CreatedAt = now.AddDays(-_random.Next(1, 180)),
            };

            var result = await userManager.CreateAsync(user, "Test1234!");
            if (result.Succeeded)
                users.Add(user);
        }

        return users;
    }

    private static List<Pet> SeedPets(SNIFContext context, List<User> users)
    {
        var pets = new List<Pet>();
        var now = DateTime.UtcNow;
        int petIndex = 0;

        // Distribute ~200 pets: some users get 1, some 2, some 3
        // Pattern: first 40 users get 2 each (80), next 40 get 2 each (80), last 20 get 2 each (40) = 200
        // For variety: 20 get 1, 60 get 2, 20 get 3 => 20 + 120 + 60 = 200
        var petCounts = new int[100];
        for (int i = 0; i < 20; i++) petCounts[i] = 1;
        for (int i = 20; i < 80; i++) petCounts[i] = 2;
        for (int i = 80; i < 100; i++) petCounts[i] = 3;

        for (int i = 0; i < users.Count; i++)
        {
            var user = users[i];
            for (int j = 0; j < petCounts[i]; j++)
            {
                var species = PickSpecies(petIndex);
                var breed = PickBreed(species);
                var petId = Guid.NewGuid().ToString();
                var purposes = PickPurposes();
                var personalities = PickPersonalities();
                var age = species switch
                {
                    "Dog" => _random.Next(0, 16),
                    "Cat" => _random.Next(0, 16),
                    "Rabbit" => _random.Next(0, 10),
                    "Hamster" => _random.Next(0, 4),
                    "Bird" => _random.Next(0, 15),
                    _ => _random.Next(0, 10)
                };

                var pet = new Pet
                {
                    Id = petId,
                    Name = PetNames[petIndex % PetNames.Length],
                    Species = species,
                    Breed = breed,
                    Age = age,
                    Gender = _random.NextDouble() > 0.5 ? Gender.Male : Gender.Female,
                    Purpose = purposes,
                    Personality = personalities,
                    Photos = new List<string>
                    {
                        $"https://picsum.photos/seed/pet-{petId.Substring(0, 8)}-1/400/400",
                        $"https://picsum.photos/seed/pet-{petId.Substring(0, 8)}-2/400/400"
                    },
                    Videos = new List<string>(),
                    Location = user.Location != null
                        ? new Location
                        {
                            Latitude = user.Location.Latitude + (_random.NextDouble() - 0.5) * 0.01,
                            Longitude = user.Location.Longitude + (_random.NextDouble() - 0.5) * 0.01,
                            City = user.Location.City,
                            Country = user.Location.Country,
                            CreatedAt = now
                        }
                        : null,
                    OwnerId = user.Id,
                    CreatedAt = now.AddDays(-_random.Next(1, 120)),
                    UpdatedAt = now
                };

                // ~70% have medical history
                if (_random.NextDouble() < 0.7)
                {
                    pet.MedicalHistory = new MedicalHistory
                    {
                        IsVaccinated = _random.NextDouble() > 0.15,
                        HealthIssues = _random.NextDouble() < 0.3
                            ? new List<string> { HealthIssues[_random.Next(HealthIssues.Length)] }
                            : new List<string>(),
                        VaccinationRecords = new List<string> { "Rabies", "DHPP" },
                        LastCheckup = now.AddDays(-_random.Next(30, 365)),
                        VetContact = $"+{(_random.NextDouble() > 0.5 ? "40" : "36")} {_random.Next(100, 999)} {_random.Next(100, 999)} {_random.Next(100, 999)}",
                        CreatedAt = now
                    };
                }

                pets.Add(pet);
                petIndex++;
            }
        }

        return pets;
    }

    private static List<PetMedia> SeedPetMedia(List<Pet> pets)
    {
        var media = new List<PetMedia>();
        var now = DateTime.UtcNow;

        foreach (var pet in pets)
        {
            if (pet.Photos == null) continue;
            var photos = pet.Photos.ToList();
            for (int i = 0; i < photos.Count; i++)
            {
                media.Add(new PetMedia
                {
                    Id = Guid.NewGuid().ToString(),
                    PetId = pet.Id,
                    FileName = photos[i],
                    ContentType = "image/jpeg",
                    Size = 0,
                    Type = MediaType.Photo,
                    Title = $"{pet.Name} photo {i + 1}",
                    Description = string.Empty,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }

        return media;
    }

    private static List<Match> SeedMatches(SNIFContext context, List<Pet> pets)
    {
        var matches = new List<Match>();
        var now = DateTime.UtcNow;
        var usedPairs = new HashSet<string>();

        for (int i = 0; i < 30; i++)
        {
            Pet initiator, target;
            string pairKey;
            int attempts = 0;

            do
            {
                initiator = pets[_random.Next(pets.Count)];
                target = pets[_random.Next(pets.Count)];
                pairKey = $"{initiator.Id}-{target.Id}";
                attempts++;
            } while ((initiator.OwnerId == target.OwnerId || usedPairs.Contains(pairKey)) && attempts < 100);

            if (attempts >= 100) continue;
            usedPairs.Add(pairKey);
            usedPairs.Add($"{target.Id}-{initiator.Id}");

            var status = i switch
            {
                < 12 => MatchStatus.Accepted,
                < 22 => MatchStatus.Pending,
                < 28 => MatchStatus.Rejected,
                _ => MatchStatus.Expired
            };

            matches.Add(new Match
            {
                Id = Guid.NewGuid().ToString(),
                InitiatiorPetId = initiator.Id,
                TargetPetId = target.Id,
                Purpose = (PetPurpose)_random.Next(0, 3),
                Status = status,
                ExpiresAt = status == MatchStatus.Pending ? now.AddDays(7) : null,
                CreatedAt = now.AddDays(-_random.Next(1, 60)),
                UpdatedAt = now
            });
        }

        return matches;
    }

    private static List<Message> SeedMessages(List<Match> matches, List<User> users, List<Pet> pets)
    {
        var messages = new List<Message>();
        var now = DateTime.UtcNow;

        var acceptedMatches = matches.Where(m => m.Status == MatchStatus.Accepted).ToList();
        var petOwnerMap = pets.ToDictionary(p => p.Id, p => p.OwnerId);

        int msgIndex = 0;
        foreach (var match in acceptedMatches)
        {
            var senderId = petOwnerMap[match.InitiatiorPetId];
            var receiverId = petOwnerMap[match.TargetPetId];
            var msgCount = 3 + _random.Next(0, 5); // 3-7 messages per accepted match

            for (int j = 0; j < msgCount && msgIndex < 50; j++)
            {
                var isSender = j % 2 == 0;
                messages.Add(new Message
                {
                    Id = Guid.NewGuid().ToString(),
                    Content = ChatMessages[msgIndex % ChatMessages.Length],
                    SenderId = isSender ? senderId : receiverId,
                    ReceiverId = isSender ? receiverId : senderId,
                    MatchId = match.Id,
                    IsRead = _random.NextDouble() > 0.3,
                    CreatedAt = now.AddMinutes(-_random.Next(10, 10000)),
                    UpdatedAt = now
                });
                msgIndex++;
            }
        }

        return messages;
    }

    #region Data

    private static (string First, string Last) GetName(int index)
    {
        // Mix of Romanian and Hungarian names
        return (FirstNames[index % FirstNames.Length], LastNames[index % LastNames.Length]);
    }

    private static readonly string[] FirstNames =
    {
        // Romanian
        "Andrei", "Maria", "Alexandru", "Elena", "Ion", "Ana", "Mihai", "Ioana",
        "Cristian", "Gabriela", "Florin", "Raluca", "Adrian", "Diana", "Bogdan", "Simona",
        "Vlad", "Alina", "Dragoș", "Mădălina",
        // Hungarian
        "László", "Katalin", "István", "Éva", "János", "Zsuzsa", "Gábor", "Ágnes",
        "Ferenc", "Margit", "Péter", "Ildikó", "Zoltán", "Anna", "Tamás", "Réka",
        "Bálint", "Boglárka", "Attila", "Nóra",
        // More Romanian
        "Radu", "Claudia", "Lucian", "Roxana", "Nicolae", "Camelia", "Dan", "Irina",
        "George", "Andreea", "Cosmin", "Lavinia", "Stelian", "Bianca", "Mircea", "Delia",
        // More Hungarian
        "Endre", "Hajnalka", "Levente", "Dorina", "Csaba", "Vivien", "Sándor", "Enikő",
        "Norbert", "Kinga", "Dávid", "Virág"
    };

    private static readonly string[] LastNames =
    {
        // Romanian
        "Popescu", "Ionescu", "Popa", "Dumitru", "Stan", "Stoica", "Gheorghe",
        "Rusu", "Munteanu", "Matei", "Constantin", "Dumitrescu", "Moldovan",
        "Radu", "Nistor", "Balan", "Ciobanu", "Luca", "Florea", "Toma",
        // Hungarian
        "Nagy", "Kovács", "Tóth", "Szabó", "Horváth", "Varga", "Kiss",
        "Molnár", "Németh", "Farkas", "Balogh", "Papp", "Takács",
        "Juhász", "Lakatos", "Mészáros", "Oláh", "Simon", "Rácz", "Fehér",
        // More mixed
        "Todea", "Bogdan", "Szilágyi", "Hajdu", "Pintér", "Antal",
        "Szűcs", "Cseh", "Dragomir", "Petrescu"
    };

    private record CityInfo(string Name, string Country, double Lat, double Lng);

    private static readonly CityInfo[] Cities =
    {
        // Romania
        new("Bucharest", "Romania", 44.4268, 26.1025),
        new("Cluj-Napoca", "Romania", 46.7712, 23.6236),
        new("Timișoara", "Romania", 45.7489, 21.2087),
        new("Iași", "Romania", 47.1585, 27.6014),
        new("Constanța", "Romania", 44.1598, 28.6348),
        new("Brașov", "Romania", 45.6427, 25.5887),
        new("Sibiu", "Romania", 45.7983, 24.1256),
        new("Oradea", "Romania", 47.0465, 21.9189),
        new("Craiova", "Romania", 44.3190, 23.7965),
        new("Târgu Mureș", "Romania", 46.5386, 24.5575),
        // Hungary
        new("Budapest", "Hungary", 47.4979, 19.0402),
        new("Debrecen", "Hungary", 47.5316, 21.6273),
        new("Szeged", "Hungary", 46.2530, 20.1414),
        new("Miskolc", "Hungary", 48.1035, 20.7784),
        new("Pécs", "Hungary", 46.0727, 18.2323),
        new("Győr", "Hungary", 47.6875, 17.6504),
        new("Nyíregyháza", "Hungary", 47.9554, 21.7168),
        new("Kecskemét", "Hungary", 46.8964, 19.6913),
        new("Székesfehérvár", "Hungary", 47.1860, 18.4221),
    };

    private static readonly string[] PetNames =
    {
        "Max", "Luna", "Buddy", "Bella", "Charlie", "Daisy", "Rocky", "Lola",
        "Cooper", "Sadie", "Bear", "Molly", "Duke", "Maggie", "Tucker", "Sophie",
        "Jack", "Chloe", "Oliver", "Lucy", "Milo", "Zoey", "Leo", "Lily",
        "Biscuit", "Coco", "Simba", "Nala", "Ginger", "Pepper", "Teddy", "Rosie",
        "Bruno", "Mia", "Rex", "Ruby", "Oscar", "Willow", "Toby", "Hazel",
        "Finn", "Stella", "Zeus", "Penny", "Mango", "Kira", "Archie", "Bonnie",
        "Scout", "Olive"
    };

    private static string PickSpecies(int index)
    {
        var roll = _random.NextDouble();
        if (roll < 0.60) return "Dog";
        if (roll < 0.90) return "Cat";
        return _random.NextDouble() < 0.33 ? "Rabbit" : (_random.NextDouble() < 0.5 ? "Hamster" : "Bird");
    }

    private static string PickBreed(string species) => species switch
    {
        "Dog" => DogBreeds[_random.Next(DogBreeds.Length)],
        "Cat" => CatBreeds[_random.Next(CatBreeds.Length)],
        "Rabbit" => RabbitBreeds[_random.Next(RabbitBreeds.Length)],
        "Hamster" => HamsterBreeds[_random.Next(HamsterBreeds.Length)],
        "Bird" => BirdBreeds[_random.Next(BirdBreeds.Length)],
        _ => "Mixed"
    };

    private static readonly string[] DogBreeds =
    {
        "Golden Retriever", "Labrador", "German Shepherd", "Bulldog", "Poodle",
        "Beagle", "Husky", "Rottweiler", "Dachshund", "Boxer",
        "Yorkshire Terrier", "Shih Tzu", "Border Collie", "Cocker Spaniel", "Pomeranian"
    };

    private static readonly string[] CatBreeds =
    {
        "Persian", "Siamese", "British Shorthair", "Maine Coon", "Ragdoll",
        "Bengal", "Sphynx", "Scottish Fold", "Abyssinian", "Russian Blue"
    };

    private static readonly string[] RabbitBreeds =
        { "Holland Lop", "Mini Rex", "Netherland Dwarf", "Lionhead", "Flemish Giant" };

    private static readonly string[] HamsterBreeds =
        { "Syrian", "Dwarf Campbell", "Dwarf Winter White", "Roborovski", "Chinese" };

    private static readonly string[] BirdBreeds =
        { "Budgerigar", "Cockatiel", "Lovebird", "Canary", "Parrot" };

    private static List<AnimalBreed> SeedAnimalBreeds()
    {
        var breeds = new List<AnimalBreed>();

        void AddBreeds(string species, string[] breedList)
        {
            foreach (var b in breedList)
            {
                breeds.Add(new AnimalBreed
                {
                    Id = Guid.NewGuid().ToString(),
                    Species = species,
                    Name = b,
                    IsCustom = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }

        AddBreeds("Dog", new[] {
            "Affenpinscher", "Afghan Hound", "Airedale Terrier", "Akita", "Alaskan Malamute", "American Bulldog",
            "American Pit Bull Terrier", "American Staffordshire Terrier", "Australian Cattle Dog", "Australian Shepherd",
            "Basset Hound", "Beagle", "Belgian Malinois", "Bernese Mountain Dog", "Bichon Frise", "Bloodhound",
            "Border Collie", "Boston Terrier", "Boxer", "Bulldog", "Bullmastiff", "Cane Corso", "Cavalier King Charles Spaniel",
            "Chihuahua", "Chow Chow", "Cocker Spaniel", "Collie", "Corgi", "Dachshund", "Dalmatian", "Doberman Pinscher",
            "English Springer Spaniel", "French Bulldog", "German Shepherd", "German Shorthaired Pointer", "Golden Retriever",
            "Great Dane", "Great Pyrenees", "Greyhound", "Havanese", "Husky", "Jack Russell Terrier", "Labrador Retriever",
            "Maltese", "Mastiff", "Miniature Schnauzer", "Newfoundland", "Papillon", "Pekingese", "Pomeranian", "Poodle",
            "Pug", "Rhodesian Ridgeback", "Rottweiler", "Saint Bernard", "Samoyed", "Shetland Sheepdog", "Shiba Inu",
            "Shih Tzu", "Siberian Husky", "Staffordshire Bull Terrier", "Vizsla", "Weimaraner", "Whippet", "Yorkshire Terrier",
            "Mixed / Mutt"
        });

        AddBreeds("Cat", new[] {
            "Abyssinian", "American Bobtail", "American Curl", "American Shorthair", "American Wirehair", "Balinese",
            "Bengal", "Birman", "Bombay", "British Shorthair", "Burmese", "Burmilla", "Chartreux", "Colorpoint Shorthair",
            "Cornish Rex", "Devon Rex", "Egyptian Mau", "European Shorthair", "Exotic Shorthair", "Havana Brown",
            "Himalayan", "Japanese Bobtail", "Khao Manee", "Korat", "LaPerm", "Lykoi", "Maine Coon", "Manx",
            "Munchkin", "Nebelung", "Norwegian Forest Cat", "Ocicat", "Oriental", "Persian", "Peterbald", "Pixiebob",
            "Ragamuffin", "Ragdoll", "Russian Blue", "Savannah", "Scottish Fold", "Selkirk Rex", "Siamese", "Siberian",
            "Singapura", "Snowshoe", "Somali", "Sphynx", "Tonkinese", "Toyger", "Turkish Angora", "Turkish Van",
            "Domestic Shorthair (Mixed)", "Domestic Mediumhair (Mixed)", "Domestic Longhair (Mixed)"
        });

        AddBreeds("Rabbit", new[] {
            "American", "American Chinchilla", "American Fuzzy Lop", "Angora", "Belgian Hare", "Beveren",
            "Blanc de Hotot", "Britannia Petite", "Californian", "Champagne d'Argent", "Checkered Giant",
            "Cinnamon", "Dutch", "Dwarf Hotot", "English Lop", "English Spot", "Flemish Giant", "Florida White",
            "French Lop", "Harlequin", "Havana", "Himalayan", "Holland Lop", "Jersey Wooly", "Lilac", "Lionhead",
            "Mini Lop", "Mini Rex", "Mini Satin", "Netherland Dwarf", "New Zealand", "Palomino", "Polish", "Rex",
            "Rhinelander", "Satin", "Silver", "Silver Fox", "Silver Marten", "Standard Chinchilla", "Mixed"
        });

        AddBreeds("Horse", new[] {
            "Akhal-Teke", "American Paint Horse", "American Quarter Horse", "American Saddlebred", "Andalusian",
            "Appaloosa", "Arabian", "Belgian Draft", "Clydesdale", "Dutch Warmblood", "Friesian", "Hanoverian",
            "Icelandic Horse", "Lipizzan", "Miniature Horse", "Morgan", "Mustang", "Oldenburg", "Paso Fino",
            "Percheron", "Pony of the Americas", "Shetland Pony", "Shire", "Standardbred", "Tennessee Walking Horse",
            "Thoroughbred", "Trakehner", "Welsh Pony", "Grade / Mixed"
        });

        AddBreeds("Bird", new[] {
            "African Grey Parrot", "Amazon Parrot", "Budgerigar (Parakeet)", "Caique", "Canary", "Cockatiel",
            "Cockatoo", "Conure", "Dove", "Eclectus Parrot", "Finch", "Lorie / Lorikeet", "Lovebird", "Macaw",
            "Parrotlet", "Pigeon", "Pionus Parrot", "Quaker Parrot", "Toucan", "Mixed"
        });

        return breeds;
    }

    private static readonly string[] PersonalityTraits =
    {
        "Playful", "Calm", "Energetic", "Friendly", "Shy",
        "Loyal", "Independent", "Curious", "Gentle", "Protective"
    };

    private static readonly string[] HealthIssues =
    {
        "Allergies", "Hip dysplasia", "Dental issues", "Ear infections",
        "Obesity", "Arthritis", "Skin conditions"
    };

    private static List<PetPurpose> PickPurposes()
    {
        var purposes = Enum.GetValues<PetPurpose>();
        var count = 1 + _random.Next(0, 3);
        return purposes.OrderBy(_ => _random.Next()).Take(count).Distinct().ToList();
    }

    private static List<string> PickPersonalities()
    {
        var count = 2 + _random.Next(0, 4);
        return PersonalityTraits.OrderBy(_ => _random.Next()).Take(count).ToList();
    }

    private static readonly string[] ChatMessages =
    {
        "Hi! I saw your pet's profile, they look adorable!",
        "Thank you! Yours is so cute too!",
        "Would you like to arrange a playdate this weekend?",
        "That sounds great! Where do you usually take them for walks?",
        "There's a nice park near the city center, we could meet there.",
        "Perfect! What time works for you?",
        "How about Saturday around 10am?",
        "That works! My dog loves meeting new friends.",
        "Mine too! Should I bring any toys?",
        "Sure, the more the merrier!",
        "Is your pet good with other animals?",
        "Yes, very friendly and socialized from a young age.",
        "That's wonderful to hear!",
        "Looking forward to it!",
        "Same here! See you soon!",
        "Hey, how's your pet doing?",
        "Great, thanks for asking! Yours?",
        "Doing well! We just came back from the vet, all healthy.",
        "That's good to hear. Regular checkups are important.",
        "Absolutely! Have a great day!",
        "Do you know any good pet-friendly cafés nearby?",
        "Yes! There's one on the main street, very welcoming.",
        "I'd love to check it out sometime.",
        "We should definitely go together with our pets!",
        "That would be lovely!",
        "What's your pet's favorite treat?",
        "She loves cheese and chicken strips!",
        "Ha! Mine goes crazy for peanut butter.",
        "Classic! Maybe we can swap some treat ideas.",
        "For sure! Talk soon!",
        "Hi again! Just wanted to confirm our meetup.",
        "Yes, still on! Can't wait.",
        "Great, see you at the park at 10!",
        "My cat has been extra playful lately.",
        "That's adorable! What breed is she?",
        "She's a British Shorthair, very fluffy.",
        "Oh I love those! So cuddly looking.",
        "She really is! A big softie.",
        "Would love to see more photos!",
        "I'll share some on the app!",
        "Does your pet get along with cats?",
        "Yes, surprisingly well! They're quite curious.",
        "That's rare and lovely.",
        "We've been socializing them since they were young.",
        "It really shows! Well done.",
        "Thanks! Let's plan something for next week.",
        "Absolutely, I'll check my schedule.",
        "Sounds like a plan!",
        "Take care until then!",
        "You too! Give your pet a treat from me! 🐾"
    };

    #endregion
}
