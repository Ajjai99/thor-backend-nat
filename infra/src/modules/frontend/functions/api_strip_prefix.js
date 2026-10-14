// Viewer-request on the /api/* behavior: "/api/v1/orders" -> "/v1/orders". The path pattern
// guarantees the "/api/" prefix, so dropping the first four characters is exact.
function handler(event) {
  var request = event.request;
  request.uri = request.uri.substring(4);
  return request;
}
