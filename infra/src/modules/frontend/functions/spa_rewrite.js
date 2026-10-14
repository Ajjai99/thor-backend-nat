// Viewer-request on the default (SPA bucket) behavior: client-side routes have no file extension,
// so they get the SPA shell. Real assets pass through untouched, so a missing one is a genuine
// 403/404 rather than index.html served as a 200.
function handler(event) {
  var request = event.request;
  var lastSegment = request.uri.substring(request.uri.lastIndexOf('/') + 1);
  if (lastSegment.indexOf('.') === -1) {
    request.uri = '/index.html';
  }
  return request;
}
