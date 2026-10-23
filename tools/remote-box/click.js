ObjC.import('CoreGraphics');
function run(argv){
  var p = $.CGPointMake(parseFloat(argv[0]), parseFloat(argv[1]));
  var mv = $.CGEventCreateMouseEvent($(), $.kCGEventMouseMoved, p, $.kCGMouseButtonLeft);
  $.CGEventPost($.kCGHIDEventTap, mv); delay(0.15);
  var d = $.CGEventCreateMouseEvent($(), $.kCGEventLeftMouseDown, p, $.kCGMouseButtonLeft);
  $.CGEventPost($.kCGHIDEventTap, d); delay(0.08);
  var u = $.CGEventCreateMouseEvent($(), $.kCGEventLeftMouseUp, p, $.kCGMouseButtonLeft);
  $.CGEventPost($.kCGHIDEventTap, u);
  return "clicked " + argv[0] + "," + argv[1];
}
