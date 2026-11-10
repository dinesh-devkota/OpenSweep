package io.opensweep.android;

import android.provider.DocumentsContract.Document;

import org.junit.Test;

import java.util.Arrays;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

public class FolderCleanerTest {
    private static final String MIME = "application/octet-stream";
    private static final int DELETE = Document.FLAG_SUPPORTS_DELETE;

    private static FolderCleaner.Metadata file(String name) {
        return new FolderCleaner.Metadata("a", name, MIME, DELETE, 10, 25);
    }

    @Test public void matchesOnlyNamedJunkAndTheLastTmpExtension() {
        for (String name : new String[]{"job.TmP", "Thumbs.db", ".DS_STORE"}) {
            assertTrue(name, file(name).eligible());
        }
        for (String name : new String[]{"photo.jpg", "work.tmp.doc", "myThumbs.db", "notes.log",
                "partial.crdownload", ".DS_Store.backup", "tm", ""}) {
            assertFalse(name, file(name).eligible());
        }
    }

    @Test public void directoriesReadOnlyVirtualAndPartialFilesAreNeverCandidates() {
        assertFalse(new FolderCleaner.Metadata("a", "job.tmp", Document.MIME_TYPE_DIR, DELETE, 10, 25).eligible());
        for (int flags : new int[]{0, Document.FLAG_SUPPORTS_WRITE, DELETE | Document.FLAG_PARTIAL,
                DELETE | Document.FLAG_VIRTUAL_DOCUMENT}) {
            assertFalse(new FolderCleaner.Metadata("a", "job.tmp", MIME, flags, 10, 25).eligible());
        }
        assertFalse(new FolderCleaner.Metadata("a", "job.tmp", null, DELETE, 10, 25).eligible());
    }

    @Test public void movedDocumentsWithStableIdsDoNotMatchThePreviewPath() {
        FolderCleaner.Location tree = new FolderCleaner.Location(null, "root", "");
        FolderCleaner.Location dir = new FolderCleaner.Location(tree, "1", "exports");
        FolderCleaner.Location item = new FolderCleaner.Location(dir, "2", "report.tmp");
        assertEquals("exports/report.tmp", item.relativePath());
        assertTrue(item.matchesIds(Arrays.asList("root", "1", "2")));
        assertFalse(item.matchesIds(Arrays.asList("root", "2")));
        assertFalse(item.matchesIds(Arrays.asList("root", "3", "2")));
        assertFalse(item.matchesIds(Arrays.asList("root", "3", "1", "2")));
    }

    @Test public void deletionRequiresTheSameDocumentToStillMatchThePreview() {
        FolderCleaner.Metadata original = file("job.tmp");
        assertTrue(original.stillMatches(file("job.tmp")));
        assertFalse(original.stillMatches(file("other.tmp")));
        assertFalse(original.stillMatches(new FolderCleaner.Metadata("b", "job.tmp", MIME, DELETE, 10, 25)));
        assertFalse(original.stillMatches(new FolderCleaner.Metadata("a", "job.tmp", MIME, DELETE, 11, 25)));
        assertFalse(original.stillMatches(new FolderCleaner.Metadata("a", "job.tmp", MIME, DELETE, 10, 26)));
        assertFalse(original.stillMatches(new FolderCleaner.Metadata("a", "job.tmp", "text/plain", DELETE, 10, 25)));
        assertFalse(original.stillMatches(new FolderCleaner.Metadata("a", "job.tmp", Document.MIME_TYPE_DIR, DELETE, 10, 25)));
        assertFalse(original.stillMatches(new FolderCleaner.Metadata("a", "job.tmp", MIME, 0, 10, 25)));
    }
}
