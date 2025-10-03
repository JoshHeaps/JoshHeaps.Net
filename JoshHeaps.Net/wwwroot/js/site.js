window.onload = function () {
    const element = document.getElementById("WelcomeMessage");
    element.style.display = "none"; // Trigger reflow
    element.offsetHeight; // Force reflow
    element.style.display = ""; // Restore the original display
    typeText("#WelcomeMessage", "Hey, you found me...!?``````````````````````No that's stupid````````````````Just````uhhhh`````Hello! My name is Josh. I'm a software engineer, and I try to have a lot of fun with what I do. While I have your attention, why don't I tell you more about myself?");
}