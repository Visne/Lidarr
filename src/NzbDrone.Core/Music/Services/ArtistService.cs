using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music.Events;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Music
{
    public interface IArtistService
    {
        Artist GetArtist(int artistId);
        Artist GetArtistByMetadataId(int artistMetadataId);
        List<Artist> GetArtists(IEnumerable<int> artistIds);
        Artist AddArtist(Artist newArtist, bool doRefresh);
        List<Artist> AddArtists(List<Artist> newArtists, bool doRefresh);
        Artist FindById(string foreignArtistId);
        Artist FindByName(string title);
        Artist FindByNameInexact(string title);
        List<Artist> GetCandidates(string title);
        void DeleteArtist(int artistId, bool deleteFiles, bool addImportListExclusion = false);
        void DeleteArtists(List<int> artistIds, bool deleteFiles, bool addImportListExclusion = false);
        List<Artist> GetAllArtists();
        Dictionary<int, List<int>> GetAllArtistsTags();
        List<Artist> AllForTag(int tagId);
        Artist UpdateArtist(Artist artist, bool publishUpdatedEvent = true);
        List<Artist> UpdateArtists(List<Artist> artist, bool useExistingRelativeFolder);
        Dictionary<int, string> AllArtistPaths();
        bool ArtistPathExists(string folder);
        void RemoveAddOptions(Artist artist);
    }

    public class ArtistService : IArtistService
    {
        private readonly IArtistRepository _artistRepository;
        private readonly IEventAggregator _eventAggregator;
        private readonly IBuildArtistPaths _artistPathBuilder;
        private readonly Logger _logger;
        private readonly ICached<List<Artist>> _cache;

        public ArtistService(IArtistRepository artistRepository,
                             IEventAggregator eventAggregator,
                             IBuildArtistPaths artistPathBuilder,
                             ICacheManager cacheManager,
                             Logger logger)
        {
            _artistRepository = artistRepository;
            _eventAggregator = eventAggregator;
            _artistPathBuilder = artistPathBuilder;
            _cache = cacheManager.GetCache<List<Artist>>(GetType());
            _logger = logger;
        }

        public Artist AddArtist(Artist newArtist, bool doRefresh)
        {
            _cache.Clear();
            _artistRepository.Insert(newArtist);
            _eventAggregator.PublishEvent(new ArtistAddedEvent(GetArtist(newArtist.Id), doRefresh));

            return newArtist;
        }

        public List<Artist> AddArtists(List<Artist> newArtists, bool doRefresh)
        {
            _cache.Clear();
            _artistRepository.InsertMany(newArtists);
            _eventAggregator.PublishEvent(new ArtistsImportedEvent(newArtists.ToList(), doRefresh));

            return newArtists;
        }

        public bool ArtistPathExists(string folder)
        {
            return _artistRepository.ArtistPathExists(folder);
        }

        public void DeleteArtist(int artistId, bool deleteFiles, bool addImportListExclusion = false)
        {
            _cache.Clear();
            var artist = _artistRepository.Get(artistId);
            _artistRepository.Delete(artistId);
            _eventAggregator.PublishEvent(new ArtistsDeletedEvent(new List<Artist> { artist }, deleteFiles, addImportListExclusion));
        }

        public void DeleteArtists(List<int> artistIds, bool deleteFiles, bool addExclusion = false)
        {
            var artistsToDelete = _artistRepository.Get(artistIds).ToList();

            _artistRepository.DeleteMany(artistIds);

            _eventAggregator.PublishEvent(new ArtistsDeletedEvent(artistsToDelete, deleteFiles, addExclusion));

            foreach (var artist in artistsToDelete)
            {
                _logger.Info("Deleted artist {0}", artist);
            }
        }

        public Artist FindById(string foreignArtistId)
        {
            return _artistRepository.FindById(foreignArtistId);
        }

        public Artist FindByName(string title)
        {
            return _artistRepository.FindByName(title.CleanArtistName());
        }

        public List<Tuple<Func<Artist, string, double>, string>> ArtistScoringFunctions(string title, string cleanTitle)
        {
            Func<Func<Artist, string, double>, string, Tuple<Func<Artist, string, double>, string>> tc = Tuple.Create;
            var scoringFunctions = new List<Tuple<Func<Artist, string, double>, string>>
            {
                tc((a, t) => a.CleanName.FuzzyMatch(t), cleanTitle),
                tc((a, t) => a.Name.FuzzyMatch(t), title),
                tc((a, t) => a.Metadata.Value.Aliases.Concat(new List<string> { a.Name }).Max(x => x.CleanArtistName().FuzzyMatch(t)), cleanTitle),
            };

            if (title.StartsWith("The ", StringComparison.CurrentCultureIgnoreCase))
            {
                scoringFunctions.Add(tc((a, t) => a.CleanName.FuzzyMatch(t), title.Substring(4).CleanArtistName()));
            }
            else
            {
                scoringFunctions.Add(tc((a, t) => a.CleanName.FuzzyMatch(t), "the" + cleanTitle));
            }

            return scoringFunctions;
        }

        public Artist FindByNameInexact(string title)
        {
            var artists = GetAllArtists();

            foreach (var func in ArtistScoringFunctions(title, title.CleanArtistName()))
            {
                var results = FindByStringInexact(artists, func.Item1, func.Item2);
                if (results.Count == 1)
                {
                    return results[0];
                }
            }

            return null;
        }

        public List<Artist> GetCandidates(string title)
        {
            var artists = GetAllArtists();
            var output = new List<Artist>();

            foreach (var func in ArtistScoringFunctions(title, title.CleanArtistName()))
            {
                output.AddRange(FindByStringInexact(artists, func.Item1, func.Item2));
            }

            return output.DistinctBy(x => x.Id).ToList();
        }

        private List<Artist> FindByStringInexact(List<Artist> artists, Func<Artist, string, double> scoreFunction, string title)
        {
            const double fuzzThreshold = 0.8;
            const double fuzzGap = 0.2;

            var sortedArtists = artists.Select(s => new
            {
                MatchProb = scoreFunction(s, title),
                Artist = s
            })
                .ToList()
                .OrderByDescending(s => s.MatchProb)
                .ToList();

            return sortedArtists.TakeWhile((x, i) => i == 0 || sortedArtists[i - 1].MatchProb - x.MatchProb < fuzzGap)
                .TakeWhile((x, i) => x.MatchProb > fuzzThreshold || (i > 0 && sortedArtists[i - 1].MatchProb > fuzzThreshold))
                .Select(x => x.Artist)
                .ToList();
        }

        public List<Artist> GetAllArtists()
        {
            return _cache.Get("GetAllArtists", () => _artistRepository.All().ToList(), TimeSpan.FromSeconds(30));
        }

        public Dictionary<int, string> AllArtistPaths()
        {
            return _artistRepository.AllArtistPaths();
        }

        public Dictionary<int, List<int>> GetAllArtistsTags()
        {
            return _artistRepository.AllArtistsTags();
        }

        public List<Artist> AllForTag(int tagId)
        {
            return GetAllArtists().Where(s => s.Tags.Contains(tagId))
                                 .ToList();
        }

        public Artist GetArtist(int artistId)
        {
            return _artistRepository.Get(artistId);
        }

        public Artist GetArtistByMetadataId(int artistMetadataId)
        {
            return _artistRepository.GetArtistByMetadataId(artistMetadataId);
        }

        public List<Artist> GetArtists(IEnumerable<int> artistIds)
        {
            return _artistRepository.Get(artistIds).ToList();
        }

        public void RemoveAddOptions(Artist artist)
        {
            _artistRepository.SetFields(artist, s => s.AddOptions);
        }

        public Artist UpdateArtist(Artist artist, bool publishUpdatedEvent = true)
        {
            _cache.Clear();
            var storedArtist = GetArtist(artist.Id);
            var updatedArtist = _artistRepository.Update(artist);

            if (publishUpdatedEvent)
            {
                _eventAggregator.PublishEvent(new ArtistEditedEvent(updatedArtist, storedArtist));
            }

            return updatedArtist;
        }

        public List<Artist> UpdateArtists(List<Artist> artist, bool useExistingRelativeFolder)
        {
            _cache.Clear();
            _logger.Debug("Updating {0} artist", artist.Count);

            foreach (var s in artist)
            {
                _logger.Trace("Updating: {0}", s.Name);

                if (!s.RootFolderPath.IsNullOrWhiteSpace())
                {
                    s.Path = _artistPathBuilder.BuildPath(s, useExistingRelativeFolder);

                    _logger.Trace("Changing path for {0} to {1}", s.Name, s.Path);
                }
                else
                {
                    _logger.Trace("Not changing path for: {0}", s.Name);
                }
            }

            _artistRepository.UpdateMany(artist);
            _logger.Debug("{0} artists updated", artist.Count);

            return artist;
        }
    }
}
