// Screenshot tabs + footer year. The page works without JavaScript (all screenshots are shown).
document.documentElement.classList.add("js");

document.addEventListener("DOMContentLoaded", () => {
  const tabs = Array.from(document.querySelectorAll(".tab"));

  function select(tab) {
    for (const t of tabs) {
      const active = t === tab;
      t.classList.toggle("active", active);
      t.setAttribute("aria-selected", String(active));
      t.tabIndex = active ? 0 : -1;
      document.getElementById(t.getAttribute("aria-controls")).classList.toggle("active", active);
    }
  }

  tabs.forEach((tab, i) => {
    tab.addEventListener("click", () => select(tab));
    tab.addEventListener("keydown", (e) => {
      if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
      const next = tabs[(i + (e.key === "ArrowRight" ? 1 : tabs.length - 1)) % tabs.length];
      select(next);
      next.focus();
    });
  });

  const year = document.getElementById("year");
  if (year) year.textContent = String(new Date().getFullYear());
});
