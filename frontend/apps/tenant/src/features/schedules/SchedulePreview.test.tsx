import { render, screen } from "@testing-library/react";
import { expect, test } from "vitest";
import { translations } from "@workerdock/ui";
import { SchedulePreview } from "./SchedulePreview";

test("shows client time first and schedule wall time with offset for repeated-hour instants", () => {
  render(<SchedulePreview
    items={[{ utc: "2026-11-01T05:30:00Z", offset: "-04:00" }, { utc: "2026-11-01T06:30:00Z", offset: "-05:00" }]}
    clientTimeZone="America/Sao_Paulo"
    language="en"
    scheduleTimeZone="America/New_York"
    t={translations.en}
  />);
  expect(screen.getByText(/11\/01\/2026, 2:30:00 AM/)).toBeInTheDocument();
  expect(screen.getByText(/11\/01\/2026, 3:30:00 AM/)).toBeInTheDocument();
  expect(screen.getByText(/11\/01\/2026, 1:30:00 AM.*-04:00/)).toBeInTheDocument();
  expect(screen.getByText(/11\/01\/2026, 1:30:00 AM.*-05:00/)).toBeInTheDocument();
  expect(screen.getByText(/2026-11-01T05:30:00Z/)).toBeInTheDocument();
});

test("shows returned offsets beside client time when client and schedule zones match", () => {
  render(<SchedulePreview
    items={[{ utc: "2026-11-01T05:30:00Z", offset: "-04:00" }, { utc: "2026-11-01T06:30:00Z", offset: "-05:00" }]}
    clientTimeZone="America/New_York"
    language="en"
    scheduleTimeZone="America/New_York"
    t={translations.en}
  />);
  expect(screen.getByText(/Client time: 11\/01\/2026, 1:30:00 AM.*-04:00/)).toBeInTheDocument();
  expect(screen.getByText(/Client time: 11\/01\/2026, 1:30:00 AM.*-05:00/)).toBeInTheDocument();
});
