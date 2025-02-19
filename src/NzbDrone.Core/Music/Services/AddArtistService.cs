using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Music
{
    public interface IAddArtistService
    {
        Artist AddArtist(Artist newArtist, bool doRefresh = true);
        List<Artist> AddArtists(List<Artist> newArtists, bool doRefresh = true, bool ignoreErrors = false);
    }

    public class AddArtistService : IAddArtistService
    {
        private readonly IArtistService _artistService;
        private readonly IArtistMetadataService _artistMetadataService;
        private readonly IProvideArtistInfo _artistInfo;
        private readonly IBuildFileNames _fileNameBuilder;
        private readonly IAddArtistValidator _addArtistValidator;
        private readonly Logger _logger;

        public AddArtistService(IArtistService artistService,
                                IArtistMetadataService artistMetadataService,
                                IProvideArtistInfo artistInfo,
                                IBuildFileNames fileNameBuilder,
                                IAddArtistValidator addArtistValidator,
                                Logger logger)
        {
            _artistService = artistService;
            _artistMetadataService = artistMetadataService;
            _artistInfo = artistInfo;
            _fileNameBuilder = fileNameBuilder;
            _addArtistValidator = addArtistValidator;
            _logger = logger;
        }

        public Artist AddArtist(Artist newArtist, bool doRefresh = true)
        {
            Ensure.That(newArtist, () => newArtist).IsNotNull();

            newArtist = AddSkyhookData(newArtist);
            newArtist = SetPropertiesAndValidate(newArtist);

            _logger.Info("Adding Artist {0} Path: [{1}]", newArtist, newArtist.Path);

            // add metadata
            _artistMetadataService.Upsert(newArtist.Metadata.Value);
            newArtist.ArtistMetadataId = newArtist.Metadata.Value.Id;

            // add the artist itself
            _artistService.AddArtist(newArtist, doRefresh);

            return newArtist;
        }

        public List<Artist> AddArtists(List<Artist> newArtists, bool doRefresh = true, bool ignoreErrors = false)
        {
            var added = DateTime.UtcNow;
            var artistsToAdd = new List<Artist>();

            foreach (var s in newArtists)
            {
                if (s.Path.IsNullOrWhiteSpace())
                {
                    _logger.Info("Adding Artist {0} Root Folder Path: [{1}]", s, s.RootFolderPath);
                }
                else
                {
                    _logger.Info("Adding Artist {0} Path: [{1}]", s, s.Path);
                }

                try
                {
                    var artist = AddSkyhookData(s);
                    artist = SetPropertiesAndValidate(artist);
                    artist.Added = added;
                    if (artistsToAdd.Any(f => f.ForeignArtistId == artist.ForeignArtistId))
                    {
                        _logger.Debug("Musicbrainz ID {0} was not added due to validation failure: Artist already exists on list", s.ForeignArtistId);
                        continue;
                    }

                    artistsToAdd.Add(artist);
                }
                catch (ValidationException ex)
                {
                    if (!ignoreErrors)
                    {
                        throw;
                    }

                    // Catch Import Errors for now until we get things fixed up
                    _logger.Debug(ex, "Failed to import id: {0} - {1}", s.Metadata.Value.ForeignArtistId, s.Metadata.Value.Name);
                }
            }

            // add metadata
            _artistMetadataService.UpsertMany(artistsToAdd.Select(x => x.Metadata.Value).ToList());
            artistsToAdd.ForEach(x => x.ArtistMetadataId = x.Metadata.Value.Id);

            return _artistService.AddArtists(artistsToAdd, doRefresh);
        }

        private Artist AddSkyhookData(Artist newArtist)
        {
            Artist artist;

            try
            {
                artist = _artistInfo.GetArtistInfo(newArtist.Metadata.Value.ForeignArtistId, newArtist.MetadataProfileId);
            }
            catch (ArtistNotFoundException)
            {
                _logger.Error("LidarrId {0} was not found, it may have been removed from Musicbrainz.", newArtist.Metadata.Value.ForeignArtistId);

                throw new ValidationException(new List<ValidationFailure>
                                              {
                                                  new ValidationFailure("MusicbrainzId", "An artist with this ID was not found", newArtist.Metadata.Value.ForeignArtistId)
                                              });
            }

            artist.ApplyChanges(newArtist);

            return artist;
        }

        private Artist SetPropertiesAndValidate(Artist newArtist)
        {
            var path = newArtist.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                var folderName = _fileNameBuilder.GetArtistFolder(newArtist);
                path = Path.Combine(newArtist.RootFolderPath, folderName);
            }

            // Disambiguate artist path if it exists already
            if (_artistService.ArtistPathExists(path))
            {
                if (newArtist.Metadata.Value.Disambiguation.IsNotNullOrWhiteSpace())
                {
                    path += $" ({newArtist.Metadata.Value.Disambiguation})";
                }

                if (_artistService.ArtistPathExists(path))
                {
                    var basepath = path;
                    var i = 0;
                    do
                    {
                        i++;
                        path = basepath + $" ({i})";
                    }
                    while (_artistService.ArtistPathExists(path));
                }
            }

            newArtist.Path = path;
            newArtist.CleanName = newArtist.Metadata.Value.Name.CleanArtistName();
            newArtist.SortName = Parser.Parser.NormalizeTitle(newArtist.Metadata.Value.Name).ToLower();
            newArtist.Added = DateTime.UtcNow;

            if (newArtist.AddOptions != null && newArtist.AddOptions.Monitor == MonitorTypes.None)
            {
                newArtist.Monitored = false;
            }

            var validationResult = _addArtistValidator.Validate(newArtist);

            if (!validationResult.IsValid)
            {
                throw new ValidationException(validationResult.Errors);
            }

            return newArtist;
        }
    }
}
