using System;
using System.IO;
using System.Linq;

namespace SnowPakTool {

	/// <summary>Validates extraction destinations without creating files or directories.</summary>
	internal static class ExtractionPath {
		private static readonly char[] __InvalidNameChars = Path.GetInvalidFileNameChars ();

		internal static string GetDestination ( string root , string entryName ) {
			if ( string.IsNullOrWhiteSpace ( entryName ) ) throw UnsafePath ( entryName );
			var relative = entryName.Replace ( '\\' , Path.DirectorySeparatorChar ).Replace ( '/' , Path.DirectorySeparatorChar );
			if ( Path.IsPathRooted ( relative ) || relative.IndexOf ( ':' ) >= 0 ) throw UnsafePath ( entryName );
			var parts = relative.Split ( Path.DirectorySeparatorChar );
			if ( parts.Any ( part => part.Length == 0 || part == "." || part == ".."
					|| part.EndsWith ( "." , StringComparison.Ordinal ) || part.EndsWith ( " " , StringComparison.Ordinal )
					|| part.IndexOfAny ( __InvalidNameChars ) >= 0 || IsDeviceName ( part ) ) ) throw UnsafePath ( entryName );
			var fullRoot = Path.GetFullPath ( root );
			var prefix = Path.EndsInDirectorySeparator ( fullRoot ) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
			var destination = Path.GetFullPath ( Path.Combine ( fullRoot , relative ) );
			var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			if ( !destination.StartsWith ( prefix , comparison ) ) throw UnsafePath ( entryName );
			RejectReparsePoints ( destination );
			return destination;
		}

		// Reject links in the output root and its ancestors as well as inside the root.
		// Rechecks reduce accidental redirection; they do not defeat concurrent link swaps.
		internal static void RejectReparsePoints ( string path ) {
			for ( var current = path; current != null; current = Path.GetDirectoryName ( current ) ) {
				FileAttributes attributes;
				try { attributes = File.GetAttributes ( current ); }
				catch ( FileNotFoundException ) { continue; }
				catch ( DirectoryNotFoundException ) { continue; }
				if ( ( attributes & FileAttributes.ReparsePoint ) != 0 ) {
					throw new InvalidDataException ( $"Extraction destination follows a filesystem link: '{current}'. Choose a physical output directory." );
				}
			}
		}

		private static bool IsDeviceName ( string part ) {
			var stem = part.Split ( '.' )[0].TrimEnd ( ' ' ).ToUpperInvariant ();
			if ( stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL"
					|| stem == "CONIN$" || stem == "CONOUT$" ) return true;
			return stem.Length == 4 && ( stem.StartsWith ( "COM" , StringComparison.Ordinal ) || stem.StartsWith ( "LPT" , StringComparison.Ordinal ) )
				&& ( stem[3] >= '1' && stem[3] <= '9' || stem[3] == '\u00b9' || stem[3] == '\u00b2' || stem[3] == '\u00b3' );
		}

		private static InvalidDataException UnsafePath ( string name ) => new InvalidDataException ( $"Unsafe cache-block extraction path: '{name}'." );

	}

}
